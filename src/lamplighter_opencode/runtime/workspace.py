"""Local workspace materialization for Lamplighter sessions."""

from __future__ import annotations

import json
import os
import secrets
import signal
import socket
import subprocess
import time
import urllib.error
import urllib.request
import uuid
from base64 import b64encode
from collections.abc import Iterable, Iterator
from contextlib import contextmanager
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any
from urllib.parse import quote, urlencode, urlparse, urlunparse

from lamplighter_opencode.contracts.models import (
    AgentChatSessionSpec,
    AgentSessionSpec,
    AgentSpec,
    AgentTurnRequest,
    AgentTurnResult,
    RuntimeEvent,
)
from lamplighter_opencode.contracts.validation import validate_contract
from lamplighter_opencode.runtime.events import append_runtime_event
from lamplighter_opencode.runtime.layout import (
    AgentLayout,
    AgentSessionLayout,
    agent_layout,
    agent_session_layout,
    materialize_agent_layout,
    materialize_agent_session_layout,
)

OPENCODE_CONFIG_MODES = {"inherit-global", "project-only", "managed"}


class OpenCodeServerError(RuntimeError):
    """Raised when the local OpenCode server API returns an unusable response."""


@dataclass(frozen=True)
class SessionWorkspace:
    """Paths created for one local Lamplighter agent session."""

    root: Path
    context_path: Path
    session_path: Path
    backend_config_path: Path
    events_path: Path
    inbox_dir: Path
    outbox_dir: Path
    artifacts_dir: Path
    logs_dir: Path
    workspace_dir: Path


def materialize_agent(
    spec: AgentSpec,
    controller_workspace: Path,
    *,
    validate_backend_environment: bool = True,
) -> AgentLayout:
    """Create one isolated agent workspace and persist its launch contract."""
    from lamplighter_opencode.backends.environment import validate_required_environment

    spec_value = spec.to_dict()
    validate_contract("agent_spec.schema.json", spec_value)
    if validate_backend_environment:
        validate_required_environment(spec.backend.required_env_vars)

    layout = materialize_agent_layout(controller_workspace, spec.agent_id)
    _write_json(layout.metadata_path, {**spec_value, "status": "allocated"})
    _write_json(layout.backend_config_path, spec.backend.to_dict())
    if opencode_config_mode() != "inherit-global":
        materialize_opencode_config(layout.root)
    return layout


def create_agent_session(
    spec: AgentChatSessionSpec,
    controller_workspace: Path,
) -> AgentSessionLayout:
    """Create one conversation under an existing agent."""
    spec_value = spec.to_dict()
    validate_contract("agent_chat_session_spec.schema.json", spec_value)
    agent = agent_layout(controller_workspace, spec.agent_id)
    if not agent.metadata_path.exists():
        raise ValueError(f"Agent is not prepared: {spec.agent_id}")

    with _agent_lifecycle_lock(agent.runtime_dir):
        layout = materialize_agent_session_layout(controller_workspace, spec.agent_id, spec.session_id)
        if layout.metadata_path.exists():
            return layout
        server = _read_json(agent.server_metadata_path)
        if real_backend_enabled():
            if _metadata_string(server, "status") != "ready":
                raise OpenCodeServerError(f"OpenCode server is not ready for {spec.agent_id}")
            session = _request_json(
                server,
                "POST",
                "/session",
                query={"directory": str(agent.workspace_dir)},
                body={"title": f"Lamplighter {spec.session_id}"},
            )
            opencode_session_id = _metadata_string(session, "id")
        else:
            opencode_session_id = f"fake_{uuid.uuid4().hex}"

        _write_json(
            layout.metadata_path,
            {
                **spec_value,
                "status": "ready",
                "opencode_session_id": opencode_session_id,
                "created_at": datetime.now(UTC).isoformat(),
            },
        )
        _write_json(layout.context_path, spec.context_package)
        append_runtime_event(
            layout.events_path,
            RuntimeEvent(
                event_type="agent_session.created",
                agent_session_id=spec.session_id,
                payload={"agent_id": spec.agent_id, "opencode_session_id": opencode_session_id},
            ),
        )
    return layout


def submit_agent_session_turn(
    request: AgentTurnRequest,
    controller_workspace: Path,
    agent_id: str,
    session_id: str,
) -> AgentTurnResult:
    """Submit a turn to a specific child session through its agent server."""
    if request.agent_id != agent_id or request.session_id != session_id:
        raise ValueError("Turn request agent_id and session_id must match the command target.")
    started_at = datetime.now(UTC).isoformat()
    if not real_backend_enabled():
        return AgentTurnResult(
            id=f"result_{uuid.uuid4().hex}",
            agent_session_id=session_id,
            request_id=request.id,
            status="completed",
            started_at=started_at,
            ended_at=datetime.now(UTC).isoformat(),
            message=f"Fake OpenCode response for `{request.instruction}`.",
        )

    agent = agent_layout(controller_workspace, agent_id)
    session = agent_session_layout(controller_workspace, agent_id, session_id)
    server = _read_json(agent.server_metadata_path)
    session_metadata = _read_json(session.metadata_path)
    if _metadata_string(server, "status") != "ready":
        raise OpenCodeServerError(f"OpenCode server is not ready for {agent_id}")
    opencode_session_id = _metadata_string(session_metadata, "opencode_session_id")
    prompt = _request_json(
        server,
        "POST",
        f"/session/{quote(opencode_session_id, safe='')}/message",
        query={"directory": str(agent.workspace_dir)},
        body={"parts": [{"type": "text", "text": request.instruction}]},
        timeout=300,
    )
    return AgentTurnResult(
        id=f"result_{uuid.uuid4().hex}",
        agent_session_id=session_id,
        request_id=request.id,
        status="completed",
        started_at=started_at,
        ended_at=datetime.now(UTC).isoformat(),
        message=_extract_text_response(prompt),
        commands_observed=[f"POST /session/{opencode_session_id}/message"],
    )


def cancel_agent_session(controller_workspace: Path, agent_id: str, session_id: str) -> dict[str, Any]:
    """Cancel one child session without stopping its parent agent."""
    layout = agent_session_layout(controller_workspace, agent_id, session_id)
    metadata = _read_json(layout.metadata_path)
    metadata["status"] = "cancelled"
    metadata["ended_at"] = datetime.now(UTC).isoformat()
    _write_json(layout.metadata_path, metadata)
    return metadata


def materialize_session_workspace(
    spec: AgentSessionSpec,
    runtime_root: Path,
    *,
    validate_backend_environment: bool = True,
) -> SessionWorkspace:
    """Create the local session layout and persist validated session files."""
    from lamplighter_opencode.backends.environment import validate_required_environment

    spec_value = spec.to_dict()
    validate_contract("agent_session_spec.schema.json", spec_value)

    if validate_backend_environment:
        validate_required_environment(spec.backend.required_env_vars)

    session_root = runtime_root / "sessions" / spec.agent_session_id
    workspace = SessionWorkspace(
        root=session_root,
        context_path=session_root / "context.json",
        session_path=session_root / "session.json",
        backend_config_path=session_root / "opencode-backend.json",
        events_path=session_root / "events.jsonl",
        inbox_dir=session_root / "inbox",
        outbox_dir=session_root / "outbox",
        artifacts_dir=session_root / "artifacts",
        logs_dir=session_root / "logs",
        workspace_dir=session_root / "workspace",
    )

    for directory in (
        workspace.inbox_dir,
        workspace.outbox_dir,
        workspace.artifacts_dir,
        workspace.logs_dir,
        workspace.workspace_dir,
    ):
        directory.mkdir(parents=True, exist_ok=True)

    _write_json(workspace.context_path, spec.context_package)
    _write_json(workspace.session_path, spec_value)
    _write_json(workspace.backend_config_path, spec.backend.to_dict())
    if opencode_config_mode() != "inherit-global":
        materialize_opencode_config(workspace.root)

    append_runtime_event(
        workspace.events_path,
        RuntimeEvent(
            event_type="agent_session.prepared",
            agent_session_id=spec.agent_session_id,
            payload={
                "workspace": str(workspace.workspace_dir),
                "backend_config": str(workspace.backend_config_path),
            },
        ),
    )

    return workspace


def submit_turn(request: AgentTurnRequest, root: Path) -> AgentTurnResult:
    """Submit one turn to fake or real OpenCode backend and normalize the result."""
    started_at = datetime.now(UTC).isoformat()
    if not real_backend_enabled():
        return AgentTurnResult(
            id=f"result_{uuid.uuid4().hex}",
            agent_session_id=request.agent_session_id,
            request_id=request.id,
            status="completed",
            started_at=started_at,
            ended_at=datetime.now(UTC).isoformat(),
            message=f"Fake OpenCode response for `{request.instruction}`.",
        )

    try:
        return submit_turn_to_opencode_server(request, root, started_at)
    except (OSError, OpenCodeServerError, ValueError, json.JSONDecodeError) as exc:
        return AgentTurnResult(
            id=f"result_{uuid.uuid4().hex}",
            agent_session_id=request.agent_session_id,
            request_id=request.id,
            status="failed",
            started_at=started_at,
            ended_at=datetime.now(UTC).isoformat(),
            message="OpenCode server request failed.",
            failure_report={"summary": "OpenCode server request failed.", "detail": str(exc)},
        )


def submit_turn_to_opencode_server(
    request: AgentTurnRequest, root: Path, started_at: str | None = None
) -> AgentTurnResult:
    """Submit one turn through a ready local opencode serve HTTP endpoint."""
    started = started_at or datetime.now(UTC).isoformat()
    root = root.resolve()
    metadata_path = root / "opencode-server.json"
    metadata = _read_json(metadata_path)
    workspace = _metadata_string(metadata, "workspace")
    status = _metadata_string(metadata, "status")
    if status != "ready":
        raise OpenCodeServerError(f"OpenCode server is not ready for {request.agent_session_id}: {status}")

    observed = []
    opencode_session_id = _optional_metadata_string(metadata, "opencode_session_id")
    if opencode_session_id is None:
        session_payload = {
            "title": f"Lamplighter {request.agent_session_id}",
        }
        session = _request_json(
            metadata,
            "POST",
            "/session",
            query={"directory": workspace},
            body=session_payload,
        )
        opencode_session_id = _metadata_string(session, "id")
        metadata["opencode_session_id"] = opencode_session_id
        metadata["opencode_session_created_at"] = datetime.now(UTC).isoformat()
        _write_json(metadata_path, metadata)
        observed.append("POST /session")

    prompt = _request_json(
        metadata,
        "POST",
        f"/session/{quote(opencode_session_id, safe='')}/message",
        query={"directory": workspace},
        body={
            "parts": [
                {
                    "type": "text",
                    "text": request.instruction,
                }
            ],
        },
        timeout=300,
    )
    observed.append(f"POST /session/{opencode_session_id}/message")

    message = _extract_text_response(prompt)
    return AgentTurnResult(
        id=f"result_{uuid.uuid4().hex}",
        agent_session_id=request.agent_session_id,
        request_id=request.id,
        status="completed",
        started_at=started,
        ended_at=datetime.now(UTC).isoformat(),
        message=message,
        commands_observed=observed,
    )


def stream_opencode_events(
    root: Path,
    *,
    limit: int | None = None,
    timeout: float = 60,
) -> list[RuntimeEvent]:
    """Subscribe to OpenCode SSE events and persist normalized runtime events."""
    root = root.resolve()
    metadata = _read_json(root / "opencode-server.json")
    workspace = _metadata_string(metadata, "workspace")
    status = _metadata_string(metadata, "status")
    if status != "ready":
        raise OpenCodeServerError(f"OpenCode server is not ready for event streaming: {status}")

    request = urllib.request.Request(
        _opencode_url(metadata, "/event", query={"directory": workspace}),
        headers={
            "Accept": "text/event-stream",
            "Authorization": _opencode_auth_header(metadata),
        },
        method="GET",
    )

    collected: list[RuntimeEvent] = []
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            for raw_event in _iter_sse_json(response):
                for event in normalize_opencode_event(raw_event, _metadata_agent_session_id(root)):
                    append_runtime_event(root / "events.jsonl", event)
                    collected.append(event)
                    if limit is not None and len(collected) >= limit:
                        return collected
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")[-2000:]
        raise OpenCodeServerError(f"GET /event failed with HTTP {exc.code}: {detail}") from exc
    except urllib.error.URLError as exc:
        raise OpenCodeServerError(f"GET /event failed: {exc}") from exc

    return collected


def normalize_opencode_event(raw_event: dict[str, Any], agent_session_id: str) -> list[RuntimeEvent]:
    """Map one OpenCode event into one or more Tradecraft runtime events."""
    event_type = _optional_metadata_string(raw_event, "type") or "unknown"
    properties = raw_event.get("properties")
    if not isinstance(properties, dict):
        properties = {}

    if event_type == "message.part.updated":
        part = properties.get("part")
        if not isinstance(part, dict):
            return [_runtime_event(agent_session_id, "opencode.event", event_type, {"raw_event": raw_event})]
        return [_normalize_part_updated(agent_session_id, event_type, part, properties.get("delta"))]

    if event_type == "message.updated":
        info = properties.get("info")
        if isinstance(info, dict) and info.get("error"):
            return [_runtime_event(agent_session_id, "agent_turn.failed", event_type, {"message": info})]
        if isinstance(info, dict):
            return [
                _runtime_event(
                    agent_session_id,
                    "agent_message.updated",
                    event_type,
                    {
                        "message_id": info.get("id"),
                        "opencode_session_id": info.get("sessionID"),
                        "role": info.get("role"),
                        "finish": info.get("finish"),
                        "cost": info.get("cost"),
                        "tokens": info.get("tokens"),
                    },
                )
            ]

    if event_type == "permission.updated":
        return [_runtime_event(agent_session_id, "agent_permission.requested", event_type, {"permission": properties})]

    if event_type == "permission.replied":
        return [_runtime_event(agent_session_id, "agent_permission.replied", event_type, properties)]

    if event_type == "session.status":
        status = properties.get("status")
        status_type = status.get("type") if isinstance(status, dict) else None
        normalized = {
            "idle": "agent_session.idle",
            "busy": "agent_session.busy",
            "retry": "agent_session.retry",
        }.get(str(status_type), "agent_session.status")
        return [_runtime_event(agent_session_id, normalized, event_type, properties)]

    if event_type == "session.idle":
        return [_runtime_event(agent_session_id, "agent_session.idle", event_type, properties)]

    if event_type == "session.error":
        return [_runtime_event(agent_session_id, "agent_turn.failed", event_type, properties)]

    if event_type == "command.executed":
        return [_runtime_event(agent_session_id, "agent_command.executed", event_type, properties)]

    if event_type == "todo.updated":
        return [_runtime_event(agent_session_id, "agent_todo.updated", event_type, properties)]

    return [_runtime_event(agent_session_id, "opencode.event", event_type, {"raw_event": raw_event})]


def start_opencode_server(
    root: Path,
    *,
    host: str = "127.0.0.1",
    port: int = 0,
    dry_run: bool = False,
    timeout_seconds: float = 15,
    metadata_path: Path | None = None,
    logs_dir: Path | None = None,
) -> dict[str, Any]:
    """Start a supervised opencode serve process and persist endpoint metadata."""
    root = root.resolve()
    workspace = root / "workspace"
    configured_log_root = os.environ.get("CHAT_THROUGH_HARNESS_LOG_ROOT")
    logs = (
        logs_dir
        or (Path(configured_log_root).expanduser().resolve() if configured_log_root else root.parent.parent / "logs")
        / "opencode"
    )
    server_metadata_path = metadata_path or root / "opencode-server.json"
    workspace.mkdir(parents=True, exist_ok=True)
    logs.mkdir(parents=True, exist_ok=True)
    stdout_log = logs / f"{root.name}.stdout.log"
    stderr_log = logs / f"{root.name}.stderr.log"

    mode = opencode_config_mode()
    if mode != "inherit-global":
        materialize_opencode_config(root)

    selected_port = port or _find_free_port(host)
    password = secrets.token_urlsafe(24)
    command = opencode_serve_command(root, host, selected_port, mode)
    endpoint = f"http://{host}:{selected_port}"
    metadata: dict[str, Any] = {
        "status": "planned" if dry_run else "starting",
        "endpoint": endpoint,
        "host": host,
        "port": selected_port,
        "pid": None,
        "auth": {
            "username": "opencode",
            "password": password,
        },
        "config_mode": mode,
        "command": _observed_command(command),
        "workspace": str(workspace),
        "stdout_log": str(stdout_log),
        "stderr_log": str(stderr_log),
        "started_at": datetime.now(UTC).isoformat(),
    }

    if dry_run:
        _write_json(server_metadata_path, metadata)
        return metadata

    stdout_file = stdout_log.open("a", encoding="utf-8")
    stderr_file = stderr_log.open("a", encoding="utf-8")
    env = opencode_environment(root, mode)
    env["OPENCODE_SERVER_USERNAME"] = "opencode"
    env["OPENCODE_SERVER_PASSWORD"] = password
    process = subprocess.Popen(
        command,
        cwd=workspace,
        stdout=stdout_file,
        stderr=stderr_file,
        text=True,
        env=env,
    )
    metadata["pid"] = process.pid
    _write_json(server_metadata_path, metadata)

    try:
        _wait_for_opencode_health(endpoint, password, timeout_seconds)
    except Exception:
        process.terminate()
        metadata["status"] = "failed"
        metadata["ended_at"] = datetime.now(UTC).isoformat()
        _write_json(server_metadata_path, metadata)
        raise

    metadata["status"] = "ready"
    metadata["ready_at"] = datetime.now(UTC).isoformat()
    _write_json(server_metadata_path, metadata)
    return metadata


def start_agent(
    layout: AgentLayout,
    *,
    host: str = "127.0.0.1",
    port: int = 0,
    dry_run: bool = False,
) -> dict[str, Any]:
    """Start the single OpenCode server owned by an agent."""
    with _agent_lifecycle_lock(layout.runtime_dir):
        if layout.server_metadata_path.exists():
            existing = _read_json(layout.server_metadata_path)
            if existing.get("status") in {"planned", "starting", "ready"}:
                return existing
        metadata = start_opencode_server(
            layout.root,
            host=host,
            port=port,
            dry_run=dry_run,
            metadata_path=layout.server_metadata_path,
            logs_dir=layout.logs_dir / "opencode",
        )
        agent = _read_json(layout.metadata_path)
        agent["status"] = metadata["status"]
        _write_json(layout.metadata_path, agent)
        return metadata


def stop_agent(layout: AgentLayout) -> dict[str, Any]:
    """Stop an agent's OpenCode server while retaining its workspace."""
    with _agent_lifecycle_lock(layout.runtime_dir):
        metadata = _read_json(layout.server_metadata_path)
        pid = metadata.get("pid")
        if isinstance(pid, int):
            try:
                os.kill(pid, signal.SIGTERM)
            except ProcessLookupError:
                pass
        metadata["status"] = "stopped"
        metadata["ended_at"] = datetime.now(UTC).isoformat()
        _write_json(layout.server_metadata_path, metadata)

        agent = _read_json(layout.metadata_path)
        agent["status"] = "stopped"
        _write_json(layout.metadata_path, agent)
        return metadata


def opencode_serve_command(root: Path, host: str, port: int, mode: str | None = None) -> list[str]:
    """Build the opencode serve command for the selected configuration mode."""
    selected_mode = mode or opencode_config_mode()
    command = [
        "opencode",
        "serve",
        "--hostname",
        host,
        "--port",
        str(port),
        "--log-level",
        "INFO",
    ]
    if selected_mode != "inherit-global":
        command.append("--pure")
    return command


def real_backend_enabled() -> bool:
    """Return whether turns should invoke the real OpenCode backend."""
    return (
        os.environ.get("LAMPLIGHTER_OPENCODE_USE_REAL_BACKEND") == "1"
        or os.environ.get("LAMPLIGHTER_OPENCODE_USE_REAL") == "1"
    )


def opencode_config_mode() -> str:
    """Return how OpenCode should resolve configuration for this run."""
    mode = os.environ.get("LAMPLIGHTER_OPENCODE_CONFIG_MODE", "inherit-global").strip().lower()
    if mode not in OPENCODE_CONFIG_MODES:
        allowed = ", ".join(sorted(OPENCODE_CONFIG_MODES))
        raise ValueError(f"Unsupported LAMPLIGHTER_OPENCODE_CONFIG_MODE={mode!r}. Expected one of: {allowed}.")
    return mode


def opencode_run_command(root: Path, instruction: str, mode: str | None = None) -> tuple[list[str], str]:
    """Build the OpenCode command for the selected configuration mode."""
    selected_mode = mode or opencode_config_mode()
    command = ["opencode", "run"]
    observed_parts = ["opencode run"]

    if selected_mode != "inherit-global":
        model = opencode_model()
        command.extend(["--pure", "--dir", str(root / "workspace"), "--model", model])
        observed_parts.extend(["--pure", "--dir <workspace>", f"--model {model}"])
    else:
        command.extend(["--dir", str(root / "workspace")])
        observed_parts.append("--dir <workspace>")
        model = inherited_opencode_model()
        if model:
            command.extend(["--model", model])
            observed_parts.append(f"--model {model}")

    command.append(instruction)
    return command, " ".join(observed_parts)


def inherited_opencode_model() -> str | None:
    """Return an optional model override while allowing global OpenCode defaults."""
    explicit = os.environ.get("LAMPLIGHTER_OPENCODE_MODEL") or os.environ.get("OPENCODE_MODEL")
    if explicit:
        return explicit

    deployment = os.environ.get("AZURE_OPENAI_DEPLOYMENT")
    if deployment:
        return f"azure/{deployment}"
    return None


def deterministic_date_prompt(expected_date: str | None = None) -> str:
    """Build a prompt with an exact expected answer for real-backend smoke tests."""
    expected = expected_date or datetime.now(UTC).date().isoformat()
    return (
        "This is a deterministic integration test. "
        f"Return exactly this date in YYYY-MM-DD format and no other text: {expected}"
    )


def deterministic_math_prompt() -> tuple[str, str]:
    """Return a deterministic prompt whose answer is not included in the prompt."""
    return "Compute 137 * 241. Return only the integer result, with no commas and no other text.", "33017"


def opencode_model() -> str:
    """Return the explicit model id the harness will allow OpenCode to use."""
    deployment = os.environ.get("AZURE_OPENAI_DEPLOYMENT")
    if not deployment:
        raise ValueError("AZURE_OPENAI_DEPLOYMENT is required for the real OpenCode backend.")
    return f"azure/{deployment}"


def validate_real_backend_environment() -> None:
    """Validate the environment required for a real OpenCode backend turn."""
    missing = [
        name
        for name in ("AZURE_OPENAI_API_KEY", "AZURE_OPENAI_ENDPOINT", "AZURE_OPENAI_DEPLOYMENT")
        if not os.environ.get(name)
    ]
    if missing:
        raise ValueError(f"Missing required real backend environment variables: {', '.join(missing)}")


def verify_azure_openai_api_key() -> str:
    """Make one direct Azure OpenAI Responses API call using the injected API key."""
    validate_real_backend_environment()
    body = json.dumps(
        {
            "model": os.environ["AZURE_OPENAI_DEPLOYMENT"],
            "input": "Return exactly OK and no other text.",
        }
    ).encode("utf-8")
    request = urllib.request.Request(
        azure_openai_responses_url(),
        data=body,
        headers={
            "Content-Type": "application/json",
            "api-key": os.environ["AZURE_OPENAI_API_KEY"],
        },
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            payload = json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")[-1000:]
        raise ValueError(f"Azure OpenAI API key verification failed with HTTP {exc.code}: {detail}") from exc
    except (OSError, json.JSONDecodeError) as exc:
        raise ValueError(f"Azure OpenAI API key verification failed: {exc}") from exc

    if payload.get("status") != "completed":
        raise ValueError(f"Azure OpenAI API key verification did not complete: {payload.get('status')}")
    return str(payload.get("id", ""))


def materialize_opencode_config(root: Path) -> Path:
    """Write the session-local OpenCode config and config directory."""
    config_dir = root / "opencode-config"
    project_dir = root / "workspace"
    for path in (
        config_dir,
        config_dir / "agent",
        config_dir / "command",
        config_dir / "plugin",
        project_dir / ".opencode",
        root / "home",
        root / "xdg-config",
        root / "xdg-data",
        root / "xdg-cache",
    ):
        path.mkdir(parents=True, exist_ok=True)

    deployment = os.environ.get("AZURE_OPENAI_DEPLOYMENT") or "deployment"
    options: dict[str, Any] = {
        "apiKey": "{env:AZURE_OPENAI_API_KEY}",
        "baseURL": azure_openai_base_url()
        if os.environ.get("AZURE_OPENAI_ENDPOINT")
        else "{env:AZURE_OPENAI_ENDPOINT}",
    }
    if os.environ.get("AZURE_OPENAI_ENDPOINT"):
        options["resourceName"] = azure_openai_resource_name()

    config: dict[str, Any] = {
        "$schema": "https://opencode.ai/config.json",
        "model": f"azure/{deployment}",
        "small_model": f"azure/{deployment}",
        "autoupdate": False,
        "share": "disabled",
        "enabled_providers": ["azure"],
        "provider": {
            "azure": {
                "options": options,
                "models": {
                    deployment: {
                        "name": deployment,
                        "modalities": {
                            "input": ["text"],
                            "output": ["text"],
                        },
                    },
                },
            },
        },
        "mcp": {},
        "plugin": [],
        "instructions": [],
        "permission": {
            "bash": "deny",
            "edit": "deny",
            "write": "deny",
        },
    }
    config_path = project_dir / "opencode.json"
    _write_json(config_path, config)
    return config_path


def isolated_opencode_environment(root: Path) -> dict[str, str]:
    """Build a project-only environment that prevents reading user-global state."""
    config_path = root / "workspace" / "opencode.json"
    config_dir = root / "opencode-config"
    env = os.environ.copy()
    env.update(
        {
            "HOME": str(root / "home"),
            "XDG_CONFIG_HOME": str(root / "xdg-config"),
            "XDG_DATA_HOME": str(root / "xdg-data"),
            "XDG_CACHE_HOME": str(root / "xdg-cache"),
            "OPENCODE_CONFIG": str(config_path),
            "OPENCODE_CONFIG_DIR": str(config_dir),
            "OPENCODE_DISABLE_AUTOUPDATE": "1",
        }
    )
    return env


def opencode_environment(root: Path, mode: str | None = None) -> dict[str, str]:
    """Build the OpenCode process environment for the selected configuration mode."""
    selected_mode = mode or opencode_config_mode()
    if selected_mode == "inherit-global":
        env = os.environ.copy()
        env["OPENCODE_DISABLE_AUTOUPDATE"] = "1"
        return env
    return isolated_opencode_environment(root)


def azure_openai_base_url() -> str:
    """Return the Azure OpenAI base URL shape expected by OpenCode."""
    endpoint = os.environ.get("AZURE_OPENAI_ENDPOINT")
    if not endpoint:
        raise ValueError("AZURE_OPENAI_ENDPOINT is required for the real OpenCode backend.")

    parsed = urlparse(endpoint)
    path = parsed.path.rstrip("/")
    if parsed.netloc.endswith(".openai.azure.com") and not path.endswith("/openai"):
        path = f"{path}/openai" if path else "/openai"
    return urlunparse((parsed.scheme, parsed.netloc, path, "", "", ""))


def azure_openai_responses_url() -> str:
    """Return the Azure OpenAI v1 Responses endpoint for API-key verification."""
    endpoint = os.environ.get("AZURE_OPENAI_ENDPOINT")
    if not endpoint:
        raise ValueError("AZURE_OPENAI_ENDPOINT is required for the real OpenCode backend.")

    parsed = urlparse(endpoint)
    path = parsed.path.rstrip("/")
    if not path.endswith("/openai/v1"):
        path = f"{path}/openai/v1" if path else "/openai/v1"
    return urlunparse((parsed.scheme, parsed.netloc, f"{path}/responses", "", "", ""))


def azure_openai_resource_name() -> str:
    """Return the Azure OpenAI resource name for the configured endpoint."""
    explicit = os.environ.get("AZURE_OPENAI_RESOURCE_NAME")
    if explicit:
        return explicit

    endpoint = os.environ.get("AZURE_OPENAI_ENDPOINT")
    if not endpoint:
        raise ValueError("AZURE_OPENAI_ENDPOINT is required for the real OpenCode backend.")

    host = urlparse(endpoint).netloc
    return host.split(".", maxsplit=1)[0]


def _find_free_port(host: str) -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind((host, 0))
        return int(sock.getsockname()[1])


def _wait_for_opencode_health(endpoint: str, password: str, timeout_seconds: float) -> None:
    deadline = time.monotonic() + timeout_seconds
    auth = b64encode(f"opencode:{password}".encode()).decode("ascii")
    last_error: Exception | None = None
    while time.monotonic() < deadline:
        request = urllib.request.Request(
            f"{endpoint}/global/health",
            headers={"Authorization": f"Basic {auth}"},
            method="GET",
        )
        try:
            with urllib.request.urlopen(request, timeout=1) as response:
                payload = json.loads(response.read().decode("utf-8"))
            if payload.get("healthy") is True:
                return
        except (OSError, urllib.error.HTTPError, urllib.error.URLError, json.JSONDecodeError) as exc:
            last_error = exc
            time.sleep(0.2)

    raise TimeoutError(f"OpenCode server did not become healthy before timeout: {last_error}")


def _request_json(
    metadata: dict[str, Any],
    method: str,
    path: str,
    *,
    query: dict[str, str] | None = None,
    body: object | None = None,
    timeout: float = 60,
) -> dict[str, Any]:
    endpoint = _metadata_string(metadata, "endpoint").rstrip("/")
    url = f"{endpoint}{path}"
    if query:
        url = f"{url}?{urlencode(query)}"

    payload = None if body is None else json.dumps(body).encode("utf-8")
    request = urllib.request.Request(
        url,
        data=payload,
        headers={
            "Accept": "application/json",
            "Authorization": _opencode_auth_header(metadata),
            "Content-Type": "application/json",
        },
        method=method,
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            value = json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")[-2000:]
        raise OpenCodeServerError(f"{method} {path} failed with HTTP {exc.code}: {detail}") from exc
    except urllib.error.URLError as exc:
        raise OpenCodeServerError(f"{method} {path} failed: {exc}") from exc

    if not isinstance(value, dict):
        raise OpenCodeServerError(f"{method} {path} returned a non-object JSON response.")
    return value


def _opencode_url(metadata: dict[str, Any], path: str, *, query: dict[str, str] | None = None) -> str:
    url = f"{_metadata_string(metadata, 'endpoint').rstrip('/')}{path}"
    if query:
        url = f"{url}?{urlencode(query)}"
    return url


def _iter_sse_json(response: Any) -> Iterable[dict[str, Any]]:
    data_lines: list[str] = []
    for raw_line in response:
        line = raw_line.decode("utf-8").rstrip("\r\n")
        if not line:
            if data_lines:
                value = json.loads("\n".join(data_lines))
                if isinstance(value, dict):
                    yield value
                data_lines = []
            continue
        if line.startswith(":"):
            continue
        if line.startswith("data:"):
            data_lines.append(line.removeprefix("data:").lstrip())

    if data_lines:
        value = json.loads("\n".join(data_lines))
        if isinstance(value, dict):
            yield value


def _normalize_part_updated(
    agent_session_id: str,
    opencode_event_type: str,
    part: dict[str, Any],
    delta: object,
) -> RuntimeEvent:
    part_type = str(part.get("type") or "unknown")
    base_payload = {
        "opencode_session_id": part.get("sessionID"),
        "message_id": part.get("messageID"),
        "part_id": part.get("id"),
        "part_type": part_type,
    }

    if part_type == "text":
        return _runtime_event(
            agent_session_id,
            "agent_turn.output_delta",
            opencode_event_type,
            {**base_payload, "delta": delta if isinstance(delta, str) else part.get("text", "")},
        )

    if part_type == "reasoning":
        return _runtime_event(
            agent_session_id,
            "agent_reasoning.delta",
            opencode_event_type,
            {**base_payload, "delta": delta if isinstance(delta, str) else part.get("text", "")},
        )

    if part_type == "tool":
        state = part.get("state")
        status = state.get("status") if isinstance(state, dict) else "unknown"
        return _runtime_event(
            agent_session_id,
            f"agent_tool.{status}",
            opencode_event_type,
            {
                **base_payload,
                "call_id": part.get("callID"),
                "tool": part.get("tool"),
                "state": state,
            },
        )

    if part_type == "step-start":
        return _runtime_event(agent_session_id, "agent_step.started", opencode_event_type, base_payload)

    if part_type == "step-finish":
        return _runtime_event(
            agent_session_id,
            "agent_step.finished",
            opencode_event_type,
            {
                **base_payload,
                "reason": part.get("reason"),
                "cost": part.get("cost"),
                "tokens": part.get("tokens"),
            },
        )

    if part_type == "retry":
        return _runtime_event(agent_session_id, "agent_turn.retry", opencode_event_type, {**base_payload, "part": part})

    return _runtime_event(agent_session_id, "opencode.event", opencode_event_type, {**base_payload, "part": part})


def _runtime_event(
    agent_session_id: str,
    event_type: str,
    opencode_event_type: str,
    payload: dict[str, Any],
) -> RuntimeEvent:
    return RuntimeEvent(
        event_type=event_type,
        agent_session_id=agent_session_id,
        payload={
            "source": "opencode",
            "opencode_event_type": opencode_event_type,
            **payload,
        },
    )


def _metadata_agent_session_id(root: Path) -> str:
    session = _read_json(root / "session.json") if (root / "session.json").exists() else {}
    value = session.get("agent_session_id") or root.name
    return str(value)


def _read_json(path: Path) -> dict[str, Any]:
    try:
        with path.open(encoding="utf-8") as input_file:
            value = json.load(input_file)
    except FileNotFoundError as exc:
        raise OpenCodeServerError(f"{path} does not exist. Run start-session before submit-turn.") from exc

    if not isinstance(value, dict):
        raise OpenCodeServerError(f"{path} must contain a JSON object.")
    return value


def _metadata_string(value: dict[str, Any], key: str) -> str:
    item = value.get(key)
    if not isinstance(item, str) or not item:
        raise OpenCodeServerError(f"OpenCode server metadata is missing string field {key!r}.")
    return item


def _optional_metadata_string(value: dict[str, Any], key: str) -> str | None:
    item = value.get(key)
    if item is None:
        return None
    if not isinstance(item, str) or not item:
        raise OpenCodeServerError(f"OpenCode server metadata has invalid string field {key!r}.")
    return item


def _opencode_auth_header(metadata: dict[str, Any]) -> str:
    auth = metadata.get("auth")
    if not isinstance(auth, dict):
        raise OpenCodeServerError("OpenCode server metadata is missing auth details.")
    username = auth.get("username")
    password = auth.get("password")
    if not isinstance(username, str) or not isinstance(password, str) or not username or not password:
        raise OpenCodeServerError("OpenCode server metadata auth details are incomplete.")
    token = b64encode(f"{username}:{password}".encode()).decode("ascii")
    return f"Basic {token}"


def _extract_text_response(value: dict[str, Any]) -> str:
    parts = value.get("parts")
    if not isinstance(parts, list):
        raise OpenCodeServerError("OpenCode prompt response did not include parts.")

    text_parts = [
        part["text"]
        for part in parts
        if isinstance(part, dict) and part.get("type") == "text" and isinstance(part.get("text"), str)
    ]
    if text_parts:
        return "\n".join(text_parts).strip()
    return json.dumps(value, sort_keys=True)


def _observed_command(command: list[str]) -> str:
    return " ".join(f'"{part}"' if " " in part else part for part in command)


def _write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f".{path.name}.{uuid.uuid4().hex}.tmp")
    try:
        with temporary.open("x", encoding="utf-8") as output:
            json.dump(value, output, indent=2, sort_keys=True)
            output.write("\n")
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


@contextmanager
def _agent_lifecycle_lock(runtime_dir: Path, timeout_seconds: float = 10) -> Iterator[None]:
    runtime_dir.mkdir(parents=True, exist_ok=True)
    lock_path = runtime_dir / ".lifecycle.lock"
    deadline = time.monotonic() + timeout_seconds
    descriptor: int | None = None
    while descriptor is None:
        try:
            descriptor = os.open(lock_path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        except FileExistsError:
            if time.monotonic() >= deadline:
                raise TimeoutError(f"Timed out waiting for agent lifecycle lock: {lock_path}") from None
            time.sleep(0.02)
    try:
        os.write(descriptor, f"{os.getpid()}\n".encode())
        yield
    finally:
        os.close(descriptor)
        lock_path.unlink(missing_ok=True)
