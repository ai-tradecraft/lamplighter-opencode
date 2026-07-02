"""Workspace materialization tests."""

import json
from pathlib import Path

from typer.testing import CliRunner

from lamplighter_opencode.cli import app
from lamplighter_opencode.contracts.models import AgentSessionSpec
from lamplighter_opencode.runtime.workspace import materialize_session_workspace


def test_materialize_session_workspace_writes_runtime_files(tmp_path: Path) -> None:
    spec = AgentSessionSpec.from_dict(_valid_session_spec())

    workspace = materialize_session_workspace(spec, tmp_path / ".agent-runtime", validate_backend_environment=False)

    assert workspace.context_path.exists()
    assert workspace.session_path.exists()
    assert workspace.backend_config_path.exists()
    assert workspace.workspace_dir.is_dir()
    assert workspace.artifacts_dir.is_dir()
    assert workspace.logs_dir.is_dir()

    events = workspace.events_path.read_text(encoding="utf-8").strip().splitlines()
    assert len(events) == 1
    assert json.loads(events[0])["event_type"] == "agent_session.prepared"


def test_prepare_session_cli_materializes_workspace(tmp_path: Path) -> None:
    spec_path = tmp_path / "session-spec.json"
    spec_path.write_text(json.dumps(_valid_session_spec()), encoding="utf-8")
    runtime_root = tmp_path / ".agent-runtime"

    result = CliRunner().invoke(
        app,
        [
            "prepare-session",
            str(spec_path),
            "--runtime-root",
            str(runtime_root),
            "--skip-backend-env-check",
        ],
    )

    assert result.exit_code == 0, result.stdout
    assert "Prepared Lamplighter session session-1" in result.stdout
    assert (runtime_root / "sessions" / "session-1" / "session.json").exists()


def _valid_session_spec() -> dict[str, object]:
    return {
        "agent_session_id": "session-1",
        "workspace_ref": "local://workspace",
        "context_package": {"goal": "test"},
        "backend": {
            "kind": "opencode",
            "server": {
                "host": "127.0.0.1",
                "port": 4096,
            },
        },
        "tool_profile": {},
        "mcp_profile": {},
        "artifact_contract": {},
        "timeout_policy": {},
        "telemetry": {},
    }
