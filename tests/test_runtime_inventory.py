"""OpenCode runtime inventory observation tests."""

from __future__ import annotations

import json
from pathlib import Path
from urllib.parse import unquote, urlparse

from typer.testing import CliRunner

import lamplighter_opencode.runtime.inventory as inventory_module
from lamplighter_opencode.cli import app
from lamplighter_opencode.contracts.validation import validate_agent_runtime_contract

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


def test_adapter_operation_inspects_runtime_inventory(tmp_path: Path) -> None:
    controller_workspace = tmp_path / "controller"
    (controller_workspace / "agents").mkdir(parents=True)
    operation_path = tmp_path / "operation.json"
    operation = {
        "message_type": "adapter.operation",
        "protocol_version": "1.0",
        "schema_version": "1.0",
        "operation_id": "cmd_inspect",
        "operation_type": "InspectRuntime",
        "idempotency_key": "inspect_one",
        "target": {"controller_id": "controller_one"},
        "deadline": "2026-07-02T00:00:00Z",
        "fencing_token": 1,
        "correlation": {"command_id": "cmd_inspect"},
        "authorization_context": {
            "subject_ref": "system://tests",
            "grant_ref": "authorization-grant://tests/1",
            "issued_at": "2026-07-02T00:00:00Z",
        },
        "extensions": {
            "tradecraft.dev/controller_workspace": str(controller_workspace),
        },
    }
    validate_agent_runtime_contract("runtime-adapter-message.schema.json", operation)
    operation_path.write_text(json.dumps(operation), encoding="utf-8")

    result = runner.invoke(app, ["adapter-operation", "--operation", str(operation_path), "--json"])

    assert result.exit_code == 0, result.stdout
    envelope = json.loads(result.stdout)
    validate_agent_runtime_contract("runtime-adapter-message.schema.json", envelope)
    assert envelope["message_type"] == "adapter.operation_result"
    result_ref = envelope["result_ref"]
    assert isinstance(result_ref, dict)
    uri = result_ref["uri"]
    assert isinstance(uri, str)
    parsed = urlparse(uri)
    assert parsed.scheme == "file"
    inventory = json.loads(Path(unquote(parsed.path)).read_text(encoding="utf-8"))
    assert inventory["adapter_kind"] == "opencode"
