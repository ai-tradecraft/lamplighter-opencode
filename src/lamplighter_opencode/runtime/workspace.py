"""Local workspace materialization for Lamplighter sessions."""

from __future__ import annotations

import json
import os
import secrets
import socket
import subprocess
import time
import urllib.error
import urllib.request
import uuid
from base64 import b64encode
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any
from urllib.parse import urlparse, urlunparse

from lamplighter_opencode.contracts.models import AgentSessionSpec, AgentTurnRequest, AgentTurnResult, RuntimeEvent
from lamplighter_opencode.contracts.validation import validate_contract
from lamplighter_opencode.runtime.events import append_runtime_event

OPENCODE_CONFIG_MODES = {"inherit-global", "project-only", "managed"}


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

    mode = opencode_config_mode()
    if mode != "inherit-global":
        validate_real_backend_environment()
        materialize_opencode_config(root)
    command, observed_command = opencode_run_command(root, request.instruction, mode)
    try:
        completed = subprocess.run(
            command,
            cwd=root / "workspace",
            check=False,
            capture_output=True,
            text=True,
            timeout=300,
            env=opencode_environment(root, mode),
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        return AgentTurnResult(
            id=f"result_{uuid.uuid4().hex}",
            agent_session_id=request.agent_session_id,
            request_id=request.id,
            status="failed",
            started_at=started_at,
            ended_at=datetime.now(UTC).isoformat(),
            message="OpenCode invocation failed.",
            failure_report={"summary": "OpenCode invocation failed.", "detail": str(exc)},
        )

    observed = [observed_command]
    if completed.returncode != 0:
        return AgentTurnResult(
            id=f"result_{uuid.uuid4().hex}",
            agent_session_id=request.agent_session_id,
            request_id=request.id,
            status="failed",
            started_at=started_at,
            ended_at=datetime.now(UTC).isoformat(),
            message=completed.stdout.strip() or "OpenCode returned a non-zero exit code.",
            commands_observed=observed,
            failure_report={
                "summary": "OpenCode returned a non-zero exit code.",
                "detail": completed.stderr[-4000:],
            },
        )

    return AgentTurnResult(
        id=f"result_{uuid.uuid4().hex}",
        agent_session_id=request.agent_session_id,
        request_id=request.id,
        status="completed",
        started_at=started_at,
        ended_at=datetime.now(UTC).isoformat(),
        message=completed.stdout.strip(),
        commands_observed=observed,
    )


def start_opencode_server(
    root: Path,
    *,
    host: str = "127.0.0.1",
    port: int = 0,
    dry_run: bool = False,
    timeout_seconds: float = 15,
) -> dict[str, Any]:
    """Start a supervised opencode serve process and persist endpoint metadata."""
    root = root.resolve()
    workspace = root / "workspace"
    logs = root / "logs"
    workspace.mkdir(parents=True, exist_ok=True)
    logs.mkdir(parents=True, exist_ok=True)

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
        "stdout_log": str(logs / "opencode-serve.stdout.log"),
        "stderr_log": str(logs / "opencode-serve.stderr.log"),
        "started_at": datetime.now(UTC).isoformat(),
    }

    if dry_run:
        _write_json(root / "opencode-server.json", metadata)
        return metadata

    stdout_file = (logs / "opencode-serve.stdout.log").open("a", encoding="utf-8")
    stderr_file = (logs / "opencode-serve.stderr.log").open("a", encoding="utf-8")
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
    _write_json(root / "opencode-server.json", metadata)

    try:
        _wait_for_opencode_health(endpoint, password, timeout_seconds)
    except Exception:
        process.terminate()
        metadata["status"] = "failed"
        metadata["ended_at"] = datetime.now(UTC).isoformat()
        _write_json(root / "opencode-server.json", metadata)
        raise

    metadata["status"] = "ready"
    metadata["ready_at"] = datetime.now(UTC).isoformat()
    _write_json(root / "opencode-server.json", metadata)
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


def _observed_command(command: list[str]) -> str:
    return " ".join(f'"{part}"' if " " in part else part for part in command)


def _write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8") as output:
        json.dump(value, output, indent=2, sort_keys=True)
        output.write("\n")
