"""OpenCode-owned runtime inventory observation."""

from __future__ import annotations

import base64
import json
import os
import urllib.error
import urllib.request
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

JsonObject = dict[str, Any]
TERMINAL_STATUSES = {"cancelled", "failed"}


def observe_runtime_inventory(controller_workspace: Path) -> JsonObject:
    """Observe OpenCode-backed runtimes and sessions under a controller workspace."""
    observed_at = _utc_now()
    runtimes = _managed_agent_inventory(controller_workspace, observed_at)
    if not runtimes:
        runtimes = _legacy_session_inventory(controller_workspace, observed_at)
    return {
        "adapter_kind": "opencode",
        "adapter_version": "0.1.0",
        "deployment_mode": "local_process",
        "observed_at": observed_at,
        "capabilities": {
            "snapshot_capture": True,
            "snapshot_restore": True,
            "restoration_modes": ["inspection"],
            "consistency_modes": ["crash-consistent"],
            "transfer_profiles": ["local-content-handle"],
        },
        "runtimes": runtimes,
    }


def _managed_agent_inventory(controller_workspace: Path, observed_at: str) -> list[JsonObject]:
    agents_root = controller_workspace / "agents"
    if not agents_root.is_dir():
        return []
    return sorted(
        (
            runtime
            for agent_root in agents_root.iterdir()
            if agent_root.is_dir()
            if (runtime := _read_managed_agent(agent_root, observed_at)) is not None
        ),
        key=lambda runtime: str(runtime.get("runtime_id") or ""),
    )


def _legacy_session_inventory(controller_workspace: Path, observed_at: str) -> list[JsonObject]:
    sessions_root = controller_workspace / "sessions"
    if not sessions_root.is_dir():
        return []
    return sorted(
        (
            runtime
            for session_root in sessions_root.iterdir()
            if session_root.is_dir()
            if (runtime := _read_legacy_session(session_root, observed_at)) is not None
        ),
        key=lambda runtime: str(runtime.get("runtime_id") or ""),
    )


def _read_managed_agent(agent_root: Path, observed_at: str) -> JsonObject | None:
    metadata = _read_json(agent_root / "agent.json")
    if metadata is None:
        return None
    agent_id = _string(metadata, "agent_id") or agent_root.name
    status = _string(metadata, "status") or "allocated"
    runtime_path = agent_root / "runtime"
    workspace_path = agent_root / "workspace"
    server_metadata = _read_json(runtime_path / "opencode-server.json")
    endpoint: str | None = None
    pid: int | None = None
    username: str | None = None
    password: str | None = None
    if server_metadata is not None:
        if status.lower() not in TERMINAL_STATUSES:
            status = _string(server_metadata, "status") or status
        endpoint = _string(server_metadata, "endpoint")
        pid = _int(server_metadata, "pid")
        auth = server_metadata.get("auth")
        if isinstance(auth, dict):
            username = _string(auth, "username")
            password = _string(auth, "password")

    if status.lower() == "ready" and endpoint:
        status = _health_status(endpoint, pid, username, password)

    sessions = _read_managed_sessions(runtime_path, observed_at)
    return {
        "runtime_id": agent_id,
        "agent_session_id": sessions[0]["session_id"] if sessions else agent_id,
        "status": status,
        "runtime_path": str(runtime_path),
        "workspace_path": str(workspace_path),
        "observed_at": observed_at,
        "provider_endpoint": endpoint,
        "provider_pid": pid,
        "sessions": sessions,
    }


def _read_managed_sessions(runtime_path: Path, observed_at: str) -> list[JsonObject]:
    sessions_root = runtime_path / "sessions"
    if not sessions_root.is_dir():
        return []
    sessions: list[JsonObject] = []
    for session_root in sessions_root.iterdir():
        if not session_root.is_dir():
            continue
        session_id = session_root.name
        status = "allocated"
        provider_session_ref: str | None = None
        metadata = _read_json(session_root / "session.json")
        if metadata is not None:
            session_id = _string(metadata, "session_id") or session_id
            status = _string(metadata, "status") or status
            provider_session_ref = _string(metadata, "opencode_session_id")
        sessions.append(
            {
                "session_id": session_id,
                "status": status,
                "runtime_path": str(session_root),
                "provider_session_ref": provider_session_ref,
                "observed_at": observed_at,
            }
        )
    return sorted(sessions, key=lambda session: str(session["session_id"]))


def _read_legacy_session(session_root: Path, observed_at: str) -> JsonObject | None:
    session_id = session_root.name
    workspace_path = session_root / "workspace"
    status = "allocated"
    metadata = _read_json(session_root / "session.json")
    if metadata is not None:
        session_id = _string(metadata, "id") or _string(metadata, "agent_session_id") or session_id
        status = _string(metadata, "status") or status
        workspace_path = Path(_string(metadata, "workspacePath") or str(workspace_path))
        if status.lower() not in TERMINAL_STATUSES and metadata.get("endedAt") is not None:
            status = "cancelled"

    endpoint: str | None = None
    pid: int | None = None
    username: str | None = None
    password: str | None = None
    server_metadata = _read_json(session_root / "opencode-server.json")
    if server_metadata is not None:
        if status.lower() not in TERMINAL_STATUSES:
            status = _string(server_metadata, "status") or status
        endpoint = _string(server_metadata, "endpoint")
        pid = _int(server_metadata, "pid")
        auth = server_metadata.get("auth")
        if isinstance(auth, dict):
            username = _string(auth, "username")
            password = _string(auth, "password")

    if status.lower() == "ready" and endpoint:
        status = _health_status(endpoint, pid, username, password)

    if not session_id:
        return None
    return {
        "runtime_id": session_id,
        "agent_session_id": session_id,
        "status": status,
        "runtime_path": str(session_root),
        "workspace_path": str(workspace_path),
        "observed_at": observed_at,
        "provider_endpoint": endpoint,
        "provider_pid": pid,
        "sessions": [],
    }


def _health_status(endpoint: str, pid: int | None, username: str | None, password: str | None) -> str:
    if not _process_running(pid):
        return "stopped"
    request = urllib.request.Request(f"{endpoint.rstrip('/')}/global/health")
    if username and password is not None:
        credentials = base64.b64encode(f"{username}:{password}".encode()).decode("ascii")
        request.add_header("Authorization", f"Basic {credentials}")
    try:
        with urllib.request.urlopen(request, timeout=2) as response:  # noqa: S310 - endpoint is adapter-owned metadata.
            if response.status < 200 or response.status >= 300:
                return "unreachable"
            payload = json.loads(response.read().decode("utf-8"))
    except (OSError, urllib.error.URLError, TimeoutError, json.JSONDecodeError):
        return "unreachable"
    return "ready" if isinstance(payload, dict) and payload.get("healthy") is True else "unreachable"


def _process_running(pid: int | None) -> bool:
    if pid is None:
        return True
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    return True


def _read_json(path: Path) -> JsonObject | None:
    if not path.is_file():
        return None
    value = json.loads(path.read_text(encoding="utf-8"))
    return value if isinstance(value, dict) else None


def _string(value: dict[str, object], key: str) -> str | None:
    found = value.get(key)
    return found if isinstance(found, str) else None


def _int(value: dict[str, object], key: str) -> int | None:
    found = value.get(key)
    return found if isinstance(found, int) else None


def _utc_now() -> str:
    return datetime.now(UTC).replace(microsecond=0).isoformat().replace("+00:00", "Z")
