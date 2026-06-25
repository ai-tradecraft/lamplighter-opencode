"""Local workspace materialization for Lamplighter sessions."""

from __future__ import annotations

import os
import subprocess
import uuid
from datetime import UTC, datetime
from pathlib import Path
from typing import Any
from urllib.parse import urlparse, urlunparse

from lamplighter_opencode.contracts.io import write_json
from lamplighter_opencode.contracts.models import AgentSessionSpec, AgentTurnRequest, AgentTurnResult, FailureReport


def session_root(spec: AgentSessionSpec) -> Path:
    workspace = Path(spec.workspace_ref).expanduser().resolve()
    return workspace.parent


def prepare_session(spec: AgentSessionSpec) -> dict[str, object]:
    root = session_root(spec)
    workspace = Path(spec.workspace_ref).expanduser().resolve()
    artifacts = Path(str(spec.artifact_contract.get("root") or root / "artifacts")).expanduser().resolve()
    logs = root / "logs"

    for path in (root, workspace, artifacts, logs, root / "inbox", root / "outbox"):
        path.mkdir(parents=True, exist_ok=True)

    write_json(root / "session.json", spec.to_dict())
    materialize_opencode_config(root)
    return {
        "agent_session_id": spec.agent_session_id,
        "status": "ready",
        "workspace_ref": str(workspace),
        "artifact_root": str(artifacts),
        "log_path": str(logs),
    }


def submit_turn(request: AgentTurnRequest, root: Path) -> AgentTurnResult:
    use_real = real_backend_enabled()
    if not use_real:
        return AgentTurnResult(
            id=f"result_{uuid.uuid4().hex}",
            agent_session_id=request.agent_session_id,
            request_id=request.id,
            status="completed",
            message=f"Fake OpenCode response for `{request.instruction}`.",
            commands_observed=[],
        )

    validate_real_backend_environment()
    materialize_opencode_config(root)
    model = opencode_model()
    try:
        completed = subprocess.run(
            [
                "opencode",
                "run",
                "--pure",
                "--dir",
                str(root / "workspace"),
                "--model",
                model,
                request.instruction,
            ],
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
            message="OpenCode invocation failed.",
            failure_report=FailureReport(summary="OpenCode invocation failed.", detail=str(exc)),
        )

    if completed.returncode != 0:
        return AgentTurnResult(
            id=f"result_{uuid.uuid4().hex}",
            agent_session_id=request.agent_session_id,
            request_id=request.id,
            status="failed",
            message=completed.stdout.strip() or "OpenCode returned a non-zero exit code.",
            commands_observed=[f"opencode run --pure --dir <workspace> --model {model}"],
            failure_report=FailureReport(
                summary="OpenCode returned a non-zero exit code.", detail=completed.stderr[-4000:]
            ),
        )

    return AgentTurnResult(
        id=f"result_{uuid.uuid4().hex}",
        agent_session_id=request.agent_session_id,
        request_id=request.id,
        status="completed",
        message=completed.stdout.strip(),
        commands_observed=[f"opencode run --pure --dir <workspace> --model {model}"],
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


def materialize_opencode_config(root: Path) -> Path:
    """Write the session-local OpenCode config and config directory."""
    config_dir = root / "opencode-config"
    project_dir = root / "workspace"
    local_opencode_dir = project_dir / ".opencode"
    for path in (
        config_dir,
        config_dir / "agent",
        config_dir / "command",
        config_dir / "plugin",
        local_opencode_dir,
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
    write_json(config_path, config)
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
