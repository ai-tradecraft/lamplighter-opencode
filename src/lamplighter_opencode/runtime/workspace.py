"""Local workspace materialization for Lamplighter sessions."""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

from lamplighter_opencode.contracts.models import AgentSessionSpec, RuntimeEvent
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


def _write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8") as output:
        json.dump(value, output, indent=2, sort_keys=True)
        output.write("\n")
