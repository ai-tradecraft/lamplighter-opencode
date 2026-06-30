"""Validated controller, agent, and session filesystem layouts."""

from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path

AGENT_ID_PATTERN = re.compile(r"^agent_[A-Za-z0-9_-]+$")
SESSION_ID_PATTERN = re.compile(r"^session_[A-Za-z0-9_-]+$")


@dataclass(frozen=True)
class ControllerLayout:
    root: Path
    metadata_path: Path
    logs_dir: Path
    agents_dir: Path


@dataclass(frozen=True)
class AgentLayout:
    root: Path
    metadata_path: Path
    workspace_dir: Path
    runtime_dir: Path
    logs_dir: Path
    sessions_dir: Path
    backend_config_path: Path
    server_metadata_path: Path


@dataclass(frozen=True)
class AgentSessionLayout:
    root: Path
    metadata_path: Path
    context_path: Path
    events_path: Path
    inbox_dir: Path
    outbox_dir: Path
    artifacts_dir: Path


def controller_layout(controller_workspace: Path) -> ControllerLayout:
    root = controller_workspace.expanduser().resolve()
    return ControllerLayout(
        root=root,
        metadata_path=root / "controller.json",
        logs_dir=root / "logs",
        agents_dir=root / "agents",
    )


def agent_layout(controller_workspace: Path, agent_id: str) -> AgentLayout:
    _validate_identifier(agent_id, AGENT_ID_PATTERN, "agent")
    controller = controller_layout(controller_workspace)
    root = _contained_child(controller.root, "agents", agent_id)
    runtime_dir = root / "runtime"
    return AgentLayout(
        root=root,
        metadata_path=root / "agent.json",
        workspace_dir=root / "workspace",
        runtime_dir=runtime_dir,
        logs_dir=runtime_dir / "logs",
        sessions_dir=runtime_dir / "sessions",
        backend_config_path=runtime_dir / "opencode-backend.json",
        server_metadata_path=runtime_dir / "opencode-server.json",
    )


def agent_session_layout(controller_workspace: Path, agent_id: str, session_id: str) -> AgentSessionLayout:
    _validate_identifier(session_id, SESSION_ID_PATTERN, "session")
    agent = agent_layout(controller_workspace, agent_id)
    root = _contained_child(agent.root, "runtime", "sessions", session_id)
    return AgentSessionLayout(
        root=root,
        metadata_path=root / "session.json",
        context_path=root / "context.json",
        events_path=root / "events.jsonl",
        inbox_dir=root / "inbox",
        outbox_dir=root / "outbox",
        artifacts_dir=root / "artifacts",
    )


def materialize_controller_layout(controller_workspace: Path) -> ControllerLayout:
    layout = controller_layout(controller_workspace)
    layout.logs_dir.mkdir(parents=True, exist_ok=True)
    layout.agents_dir.mkdir(parents=True, exist_ok=True)
    return layout


def materialize_agent_layout(controller_workspace: Path, agent_id: str) -> AgentLayout:
    materialize_controller_layout(controller_workspace)
    layout = agent_layout(controller_workspace, agent_id)
    for directory in (layout.workspace_dir, layout.logs_dir, layout.sessions_dir):
        directory.mkdir(parents=True, exist_ok=True)
    return layout


def materialize_agent_session_layout(
    controller_workspace: Path,
    agent_id: str,
    session_id: str,
) -> AgentSessionLayout:
    materialize_agent_layout(controller_workspace, agent_id)
    layout = agent_session_layout(controller_workspace, agent_id, session_id)
    for directory in (layout.inbox_dir, layout.outbox_dir, layout.artifacts_dir):
        directory.mkdir(parents=True, exist_ok=True)
    return layout


def _validate_identifier(value: str, pattern: re.Pattern[str], kind: str) -> None:
    if not pattern.fullmatch(value):
        msg = f"Invalid {kind} identifier: {value!r}"
        raise ValueError(msg)


def _contained_child(root: Path, *parts: str) -> Path:
    resolved_root = root.resolve()
    child = resolved_root.joinpath(*parts).resolve()
    if not child.is_relative_to(resolved_root):
        msg = f"Resolved path escapes controller workspace: {child}"
        raise ValueError(msg)
    return child
