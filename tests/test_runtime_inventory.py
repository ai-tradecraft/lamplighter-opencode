"""OpenCode runtime inventory observation tests."""

from __future__ import annotations

import json
from pathlib import Path

from typer.testing import CliRunner

import lamplighter_opencode.runtime.inventory as inventory_module
from lamplighter_opencode.cli import app

runner = CliRunner()


def test_observe_runtime_inventory_reports_managed_agent_sessions(tmp_path: Path, monkeypatch) -> None:
    controller_workspace = tmp_path / "controller"
    agent_root = controller_workspace / "agents" / "agent_1"
    runtime_root = agent_root / "runtime"
    (agent_root / "workspace").mkdir(parents=True)
    runtime_root.mkdir()
    (agent_root / "agent.json").write_text(json.dumps({"agent_id": "agent_1", "status": "ready"}), encoding="utf-8")
    (runtime_root / "opencode-server.json").write_text(
        json.dumps(
            {
                "status": "ready",
                "endpoint": "http://127.0.0.1:4097",
                "pid": 123,
                "auth": {"username": "opencode", "password": "secret"},
            }
        ),
        encoding="utf-8",
    )
    for session_id in ["session_1", "session_2"]:
        session_root = runtime_root / "sessions" / session_id
        session_root.mkdir(parents=True)
        (session_root / "session.json").write_text(
            json.dumps(
                {
                    "session_id": session_id,
                    "agent_id": "agent_1",
                    "status": "ready",
                    "opencode_session_id": f"opencode_{session_id}",
                }
            ),
            encoding="utf-8",
        )

    monkeypatch.setattr(inventory_module, "_health_status", lambda *args: "ready")

    inventory = inventory_module.observe_runtime_inventory(controller_workspace)

    assert inventory["adapter_kind"] == "opencode"
    runtime = inventory["runtimes"][0]
    assert runtime["runtime_id"] == "agent_1"
    assert runtime["status"] == "ready"
    assert runtime["provider_endpoint"] == "http://127.0.0.1:4097"
    assert [session["session_id"] for session in runtime["sessions"]] == ["session_1", "session_2"]


def test_observe_runtimes_command_emits_json_inventory(tmp_path: Path) -> None:
    controller_workspace = tmp_path / "controller"
    (controller_workspace / "agents").mkdir(parents=True)

    result = runner.invoke(app, ["observe-runtimes", "--controller-workspace", str(controller_workspace), "--json"])

    assert result.exit_code == 0, result.stdout
    payload = json.loads(result.stdout)
    assert payload["adapter_kind"] == "opencode"
    assert payload["runtimes"] == []
