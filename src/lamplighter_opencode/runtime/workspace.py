"""Local workspace materialization for Lamplighter sessions."""

from __future__ import annotations

import os
import subprocess
import uuid
from datetime import UTC, datetime
from pathlib import Path

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

    try:
        completed = subprocess.run(
            ["opencode", "run", request.instruction],
            cwd=root / "workspace",
            check=False,
            capture_output=True,
            text=True,
            timeout=300,
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
            commands_observed=["opencode run"],
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
        commands_observed=["opencode run"],
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
