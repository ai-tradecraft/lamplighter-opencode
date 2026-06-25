"""Local workspace materialization for Lamplighter sessions."""

from __future__ import annotations

import json
import os
import subprocess
import urllib.error
import urllib.request
import uuid
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any
from urllib.parse import urlparse, urlunparse

from lamplighter_opencode.contracts.models import AgentSessionSpec, AgentTurnRequest, AgentTurnResult, RuntimeEvent
from lamplighter_opencode.contracts.validation import validate_contract
from lamplighter_opencode.runtime.events import append_runtime_event


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

    validate_real_backend_environment()
    materialize_opencode_config(root)
    model = opencode_model()
    command = [
        "opencode",
        "run",
        "--pure",
        "--dir",
        str(root / "workspace"),
        "--model",
        model,
        request.instruction,
    ]
    try:
        completed = subprocess.run(
            command,
            cwd=root / "workspace",
            check=False,
            capture_output=True,
            text=True,
            timeout=300,
            env=isolated_opencode_environment(root),
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

    observed = [f"opencode run --pure --dir <workspace> --model {model}"]
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


def real_backend_enabled() -> bool:
    """Return whether turns should invoke the real OpenCode backend."""
    return (
        os.environ.get("LAMPLIGHTER_OPENCODE_USE_REAL_BACKEND") == "1"
        or os.environ.get("LAMPLIGHTER_OPENCODE_USE_REAL") == "1"
    )


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
    """Build an environment that prevents OpenCode from reading user-global state."""
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


def _write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8") as output:
        json.dump(value, output, indent=2, sort_keys=True)
        output.write("\n")
