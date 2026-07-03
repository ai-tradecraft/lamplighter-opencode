"""Agent lifecycle command tests."""

import hashlib
import json
import os
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from typing import cast
from urllib.parse import unquote, urlparse

from typer.testing import CliRunner

import lamplighter_opencode.runtime.workspace as workspace_module
from lamplighter_opencode.cli import app
from lamplighter_opencode.contracts.models import AgentChatSessionSpec
from lamplighter_opencode.contracts.validation import validate_agent_runtime_contract
from lamplighter_opencode.runtime.layout import agent_layout, agent_session_layout
from lamplighter_opencode.runtime.workspace import create_agent_session, start_agent

runner = CliRunner()


def test_prepare_start_and_stop_agent(tmp_path: Path, monkeypatch) -> None:
    monkeypatch.delenv("CHAT_THROUGH_HARNESS_LOG_ROOT", raising=False)
    spec_path = tmp_path / "agent-spec.json"
    spec_path.write_text(json.dumps(_agent_spec()), encoding="utf-8")
    controller_workspace = tmp_path / "controller"

    prepared = runner.invoke(
        app,
        [
            "prepare-agent",
            "--spec",
            str(spec_path),
            "--controller-workspace",
            str(controller_workspace),
            "--skip-backend-env-check",
            "--json",
        ],
    )

    assert prepared.exit_code == 0, prepared.stdout
    layout = agent_layout(controller_workspace, "agent_one")
    assert layout.workspace_dir.is_dir()
    assert layout.runtime_dir.is_dir()
    assert layout.metadata_path.is_file()

    started = runner.invoke(
        app,
        [
            "start-agent",
            "--agent",
            "agent_one",
            "--controller-workspace",
            str(controller_workspace),
            "--port",
            "4097",
            "--dry-run",
            "--json",
        ],
    )

    assert started.exit_code == 0, started.stdout
    assert layout.server_metadata_path.is_file()
    metadata = json.loads(layout.server_metadata_path.read_text(encoding="utf-8"))
    assert metadata["status"] == "planned"
    assert metadata["workspace"] == str(layout.workspace_dir)
    assert Path(metadata["stdout_log"]).is_relative_to(layout.logs_dir)

    stopped = runner.invoke(
        app,
        [
            "stop-agent",
            "--agent",
            "agent_one",
            "--controller-workspace",
            str(controller_workspace),
            "--json",
        ],
    )

    assert stopped.exit_code == 0, stopped.stdout
    assert layout.workspace_dir.is_dir()
    metadata = json.loads(layout.server_metadata_path.read_text(encoding="utf-8"))
    assert metadata["status"] == "stopped"


def test_adapter_operation_starts_runtime_from_shared_envelope(tmp_path: Path, monkeypatch) -> None:
    monkeypatch.delenv("CHAT_THROUGH_HARNESS_LOG_ROOT", raising=False)
    controller_workspace = tmp_path / "controller"
    operation_path = tmp_path / "operation.json"
    operation_path.write_text(
        json.dumps(
            _adapter_operation("StartRuntime", {"runtime_id": "agent_one"}, _agent_spec(), controller_workspace)
        ),
        encoding="utf-8",
    )

    def fake_start_agent(layout, **kwargs):  # noqa: ANN001, ANN202, ARG001
        return {
            "status": "planned",
            "endpoint": "http://127.0.0.1:4097",
            "workspace": str(layout.workspace_dir),
        }

    monkeypatch.setattr("lamplighter_opencode.cli.start_agent", fake_start_agent)

    result = runner.invoke(app, ["adapter-operation", "--operation", str(operation_path), "--json"])

    assert result.exit_code == 0, result.stdout
    envelope = json.loads(result.stdout)
    validate_agent_runtime_contract("runtime-adapter-message.schema.json", envelope)
    assert envelope["message_type"] == "adapter.operation_result"
    assert envelope["status"] == "completed"
    payload = _read_result_ref_payload(envelope)
    assert payload["agent_id"] == "agent_one"
    assert payload["status"] == "planned"
    assert agent_layout(controller_workspace, "agent_one").metadata_path.is_file()


def test_adapter_operation_submits_turn_from_shared_envelope(tmp_path: Path, monkeypatch) -> None:
    monkeypatch.delenv("LAMPLIGHTER_OPENCODE_USE_REAL_BACKEND", raising=False)
    controller_workspace = tmp_path / "controller"
    spec_path = tmp_path / "agent-spec.json"
    spec_path.write_text(json.dumps(_agent_spec()), encoding="utf-8")
    assert (
        runner.invoke(
            app,
            [
                "prepare-agent",
                "--spec",
                str(spec_path),
                "--controller-workspace",
                str(controller_workspace),
                "--skip-backend-env-check",
                "--json",
            ],
        ).exit_code
        == 0
    )
    assert (
        runner.invoke(
            app,
            [
                "start-agent",
                "--agent",
                "agent_one",
                "--controller-workspace",
                str(controller_workspace),
                "--dry-run",
                "--json",
            ],
        ).exit_code
        == 0
    )
    create_agent_session(AgentChatSessionSpec.from_dict(_session_spec("session_one")), controller_workspace)
    operation_path = tmp_path / "operation.json"
    operation_path.write_text(
        json.dumps(
            _adapter_operation(
                "StartInvocation",
                {
                    "runtime_id": "agent_one",
                    "agent_session_id": "session_one",
                    "invocation_id": "turn_one",
                },
                {
                    "id": "turn_one",
                    "agent_session_id": "session_one",
                    "agent_id": "agent_one",
                    "session_id": "session_one",
                    "type": "prompt_response",
                    "instruction": "hello",
                },
                controller_workspace,
            )
        ),
        encoding="utf-8",
    )

    result = runner.invoke(app, ["adapter-operation", "--operation", str(operation_path), "--json"])

    assert result.exit_code == 0, result.stdout
    envelope = json.loads(result.stdout)
    validate_agent_runtime_contract("runtime-adapter-message.schema.json", envelope)
    assert envelope["message_type"] == "adapter.operation_result"
    assert envelope["status"] == "completed"
    payload = _read_result_ref_payload(envelope)
    assert payload["status"] == "completed"
    message = payload["message"]
    assert isinstance(message, str)
    assert "Fake OpenCode response" in message


def test_stop_agent_cancels_nonterminal_child_sessions(tmp_path: Path, monkeypatch) -> None:
    monkeypatch.delenv("LAMPLIGHTER_OPENCODE_USE_REAL_BACKEND", raising=False)
    controller_workspace = tmp_path / "controller"
    spec_path = tmp_path / "agent-spec.json"
    spec_path.write_text(json.dumps(_agent_spec()), encoding="utf-8")
    assert (
        runner.invoke(
            app,
            [
                "prepare-agent",
                "--spec",
                str(spec_path),
                "--controller-workspace",
                str(controller_workspace),
                "--skip-backend-env-check",
                "--json",
            ],
        ).exit_code
        == 0
    )
    assert (
        runner.invoke(
            app,
            [
                "start-agent",
                "--agent",
                "agent_one",
                "--controller-workspace",
                str(controller_workspace),
                "--dry-run",
                "--json",
            ],
        ).exit_code
        == 0
    )

    for session_id in ("session_one", "session_two"):
        session_spec_path = tmp_path / f"{session_id}.json"
        session_spec_path.write_text(json.dumps(_session_spec(session_id)), encoding="utf-8")
        created = runner.invoke(
            app,
            [
                "create-session",
                "--agent",
                "agent_one",
                "--spec",
                str(session_spec_path),
                "--controller-workspace",
                str(controller_workspace),
                "--json",
            ],
        )
        assert created.exit_code == 0, created.stdout

    first = agent_session_layout(controller_workspace, "agent_one", "session_one")
    second = agent_session_layout(controller_workspace, "agent_one", "session_two")
    second_metadata = json.loads(second.metadata_path.read_text(encoding="utf-8"))
    second_metadata["status"] = "failed"
    second.metadata_path.write_text(json.dumps(second_metadata), encoding="utf-8")

    stopped = runner.invoke(
        app,
        [
            "stop-agent",
            "--agent",
            "agent_one",
            "--controller-workspace",
            str(controller_workspace),
            "--json",
        ],
    )

    assert stopped.exit_code == 0, stopped.stdout
    stop_payload = json.loads(stopped.stdout)
    assert stop_payload["closed_sessions"] == ["session_one"]
    first_metadata = json.loads(first.metadata_path.read_text(encoding="utf-8"))
    second_metadata = json.loads(second.metadata_path.read_text(encoding="utf-8"))
    assert first_metadata["status"] == "cancelled"
    assert first_metadata["closed_reason"] == "Parent agent stopped."
    assert second_metadata["status"] == "failed"


def test_agent_owns_multiple_isolated_sessions(tmp_path: Path, monkeypatch) -> None:
    monkeypatch.delenv("LAMPLIGHTER_OPENCODE_USE_REAL_BACKEND", raising=False)
    controller_workspace = tmp_path / "controller"
    spec_path = tmp_path / "agent-spec.json"
    spec_path.write_text(json.dumps(_agent_spec()), encoding="utf-8")
    assert (
        runner.invoke(
            app,
            [
                "prepare-agent",
                "--spec",
                str(spec_path),
                "--controller-workspace",
                str(controller_workspace),
                "--skip-backend-env-check",
                "--json",
            ],
        ).exit_code
        == 0
    )
    assert (
        runner.invoke(
            app,
            [
                "start-agent",
                "--agent",
                "agent_one",
                "--controller-workspace",
                str(controller_workspace),
                "--dry-run",
                "--json",
            ],
        ).exit_code
        == 0
    )

    session_metadata = []
    for session_id in ("session_one", "session_two"):
        session_spec_path = tmp_path / f"{session_id}.json"
        session_spec_path.write_text(json.dumps(_session_spec(session_id)), encoding="utf-8")
        created = runner.invoke(
            app,
            [
                "create-session",
                "--agent",
                "agent_one",
                "--spec",
                str(session_spec_path),
                "--controller-workspace",
                str(controller_workspace),
                "--json",
            ],
        )
        assert created.exit_code == 0, created.stdout
        session_metadata.append(json.loads(created.stdout))

    first = agent_session_layout(controller_workspace, "agent_one", "session_one")
    second = agent_session_layout(controller_workspace, "agent_one", "session_two")
    assert first.root != second.root
    assert session_metadata[0]["opencode_session_id"] != session_metadata[1]["opencode_session_id"]

    request_path = tmp_path / "turn.json"
    request_path.write_text(
        json.dumps(
            {
                "id": "turn_one",
                "agent_session_id": "session_one",
                "agent_id": "agent_one",
                "session_id": "session_one",
                "type": "prompt_response",
                "instruction": "hello",
            }
        ),
        encoding="utf-8",
    )
    turn = runner.invoke(
        app,
        [
            "submit-turn",
            "--agent",
            "agent_one",
            "--session",
            "session_one",
            "--request",
            str(request_path),
            "--controller-workspace",
            str(controller_workspace),
            "--json",
        ],
    )
    assert turn.exit_code == 0, turn.stdout
    assert "Fake OpenCode response" in turn.stdout

    cancelled = runner.invoke(
        app,
        [
            "cancel-session",
            "--agent",
            "agent_one",
            "--session",
            "session_one",
            "--controller-workspace",
            str(controller_workspace),
            "--json",
        ],
    )
    assert cancelled.exit_code == 0, cancelled.stdout
    assert json.loads(first.metadata_path.read_text(encoding="utf-8"))["status"] == "cancelled"
    assert json.loads(second.metadata_path.read_text(encoding="utf-8"))["status"] == "ready"
    assert json.loads(agent_layout(controller_workspace, "agent_one").metadata_path.read_text())["status"] == "planned"


def test_create_and_restore_agent_snapshot(tmp_path: Path, monkeypatch) -> None:
    monkeypatch.delenv("LAMPLIGHTER_OPENCODE_USE_REAL_BACKEND", raising=False)
    controller_workspace = tmp_path / "controller"
    spec_path = tmp_path / "agent-spec.json"
    spec_path.write_text(json.dumps(_agent_spec()), encoding="utf-8")
    runner.invoke(
        app,
        [
            "prepare-agent",
            "--spec",
            str(spec_path),
            "--controller-workspace",
            str(controller_workspace),
            "--skip-backend-env-check",
        ],
    )
    runner.invoke(
        app,
        [
            "start-agent",
            "--agent",
            "agent_one",
            "--controller-workspace",
            str(controller_workspace),
            "--dry-run",
        ],
    )
    session_spec_path = tmp_path / "session.json"
    session_spec_path.write_text(json.dumps(_session_spec("session_one")), encoding="utf-8")
    created = runner.invoke(
        app,
        [
            "create-session",
            "--agent",
            "agent_one",
            "--spec",
            str(session_spec_path),
            "--controller-workspace",
            str(controller_workspace),
        ],
    )
    assert created.exit_code == 0, created.stdout
    original = agent_layout(controller_workspace, "agent_one")
    workspace_file = original.workspace_dir / "notes" / "plan.md"
    workspace_file.parent.mkdir(parents=True)
    workspace_file.write_text("# Plan\n\nKeep this file restorable.\n", encoding="utf-8")

    snapshot = runner.invoke(
        app,
        [
            "create-snapshot",
            "--agent",
            "agent_one",
            "--session",
            "session_one",
            "--controller-workspace",
            str(controller_workspace),
            "--json",
        ],
    )

    assert snapshot.exit_code == 0, snapshot.stdout
    descriptor = json.loads(snapshot.stdout)
    assert descriptor["status"] == "content_ready"
    assert descriptor["restoration_mode"] == "inspection"
    assert descriptor["manifest"]["digest"].startswith("sha256:")
    descriptor_path = Path(descriptor["descriptor_path"])
    assert descriptor_path.exists()

    restored = runner.invoke(
        app,
        [
            "restore-snapshot",
            "--snapshot",
            str(descriptor_path),
            "--controller-workspace",
            str(controller_workspace),
            "--restored-agent",
            "agent_restored",
            "--json",
        ],
    )

    assert restored.exit_code == 0, restored.stdout
    restore_payload = json.loads(restored.stdout)
    assert restore_payload["status"] == "restored"
    assert restore_payload["resumable"] is False
    assert restore_payload["sessions_restored"] == ["session_one"]
    restored_layout = agent_layout(controller_workspace, "agent_restored")
    assert (restored_layout.workspace_dir / "notes" / "plan.md").read_text(
        encoding="utf-8"
    ) == workspace_file.read_text(encoding="utf-8")
    restored_agent = json.loads(restored_layout.metadata_path.read_text(encoding="utf-8"))
    assert restored_agent["status"] == "restored"
    restored_session = json.loads(
        agent_session_layout(controller_workspace, "agent_restored", "session_one").metadata_path.read_text(
            encoding="utf-8"
        )
    )
    assert restored_session["status"] == "restored"


def test_restore_snapshot_rejects_tampered_workspace_file(tmp_path: Path, monkeypatch) -> None:
    monkeypatch.delenv("LAMPLIGHTER_OPENCODE_USE_REAL_BACKEND", raising=False)
    controller_workspace = tmp_path / "controller"
    spec_path = tmp_path / "agent-spec.json"
    spec_path.write_text(json.dumps(_agent_spec()), encoding="utf-8")
    runner.invoke(
        app,
        [
            "prepare-agent",
            "--spec",
            str(spec_path),
            "--controller-workspace",
            str(controller_workspace),
            "--skip-backend-env-check",
        ],
    )
    layout = agent_layout(controller_workspace, "agent_one")
    (layout.workspace_dir / "file.txt").write_text("original", encoding="utf-8")
    snapshot = runner.invoke(
        app,
        [
            "create-snapshot",
            "--agent",
            "agent_one",
            "--controller-workspace",
            str(controller_workspace),
            "--json",
        ],
    )
    assert snapshot.exit_code == 0, snapshot.stdout
    descriptor = json.loads(snapshot.stdout)
    descriptor_path = Path(descriptor["descriptor_path"])
    (descriptor_path.parent / "workspace" / "file.txt").write_text("tampered", encoding="utf-8")

    restored = runner.invoke(
        app,
        [
            "restore-snapshot",
            "--snapshot",
            str(descriptor_path),
            "--controller-workspace",
            str(controller_workspace),
            "--restored-agent",
            "agent_restored",
            "--json",
        ],
    )

    assert restored.exit_code == 1
    assert "Workspace file digest mismatch" in restored.stdout


def test_start_agent_replaces_stale_ready_process_metadata(tmp_path: Path, monkeypatch) -> None:
    controller_workspace = tmp_path / "controller"
    layout = agent_layout(controller_workspace, "agent_one")
    layout.runtime_dir.mkdir(parents=True)
    layout.workspace_dir.mkdir(parents=True)
    layout.metadata_path.write_text(json.dumps({"status": "ready"}), encoding="utf-8")
    layout.server_metadata_path.write_text(
        json.dumps({"status": "ready", "pid": 999_999}),
        encoding="utf-8",
    )
    monkeypatch.setattr(os, "kill", lambda pid, signal: (_ for _ in ()).throw(ProcessLookupError()))

    def fake_start(*args, **kwargs):  # noqa: ANN002, ANN003, ANN202
        return {"status": "ready", "pid": 1234, "endpoint": "http://127.0.0.1:5000"}

    monkeypatch.setattr(workspace_module, "start_opencode_server", fake_start)

    metadata = start_agent(layout)

    assert metadata["pid"] == 1234


def test_duplicate_concurrent_session_creation_is_idempotent(tmp_path: Path, monkeypatch) -> None:
    monkeypatch.delenv("LAMPLIGHTER_OPENCODE_USE_REAL_BACKEND", raising=False)
    controller_workspace = tmp_path / "controller"
    spec_path = tmp_path / "agent-spec.json"
    spec_path.write_text(json.dumps(_agent_spec()), encoding="utf-8")
    runner.invoke(
        app,
        [
            "prepare-agent",
            "--spec",
            str(spec_path),
            "--controller-workspace",
            str(controller_workspace),
            "--skip-backend-env-check",
        ],
    )
    runner.invoke(
        app,
        [
            "start-agent",
            "--agent",
            "agent_one",
            "--controller-workspace",
            str(controller_workspace),
            "--dry-run",
        ],
    )
    spec = AgentChatSessionSpec.from_dict(_session_spec("session_same"))

    with ThreadPoolExecutor(max_workers=2) as executor:
        layouts = list(executor.map(lambda _: create_agent_session(spec, controller_workspace), range(2)))

    assert layouts[0].root == layouts[1].root
    metadata = json.loads(layouts[0].metadata_path.read_text(encoding="utf-8"))
    assert metadata["status"] == "ready"
    assert not (agent_layout(controller_workspace, "agent_one").runtime_dir / ".lifecycle.lock").exists()


def _agent_spec() -> dict[str, object]:
    return {
        "agent_id": "agent_one",
        "workspace_ref": "controller://agents/agent_one/workspace",
        "backend": {
            "kind": "opencode",
            "server": {"host": "127.0.0.1", "port": 4096},
        },
        "tool_profile": {},
        "mcp_profile": {},
        "telemetry": {},
    }


def _session_spec(session_id: str) -> dict[str, object]:
    return {
        "session_id": session_id,
        "agent_id": "agent_one",
        "context_package": {"goal": "test chat"},
        "artifact_contract": {},
        "timeout_policy": {},
        "telemetry": {},
    }


def _adapter_operation(
    operation_type: str,
    target: dict[str, object],
    payload: dict[str, object],
    controller_workspace: Path,
) -> dict[str, object]:
    operation = {
        "message_type": "adapter.operation",
        "protocol_version": "1.0",
        "schema_version": "1.0",
        "operation_id": "cmd_one",
        "operation_type": operation_type,
        "idempotency_key": "idempotency_one",
        "target": {"controller_id": "controller_one", **target},
        "deadline": "2026-07-02T00:00:00Z",
        "fencing_token": 1,
        "correlation": {"command_id": "cmd_one"},
        "authorization_context": {
            "subject_ref": "system://tests",
            "grant_ref": "authorization-grant://tests/1",
            "issued_at": "2026-07-02T00:00:00Z",
        },
        "extensions": {
            "tradecraft.dev/controller_workspace": str(controller_workspace),
        },
    }
    if operation_type == "StartInvocation":
        payload_bytes = json.dumps(payload).encode()
        payload_path = controller_workspace / "commands" / "cmd_one" / "legacy-turn-request.json"
        payload_path.parent.mkdir(parents=True, exist_ok=True)
        payload_path.write_bytes(payload_bytes)
        legacy_ref = {
            "uri": payload_path.resolve().as_uri(),
            "sha256": hashlib.sha256(payload_bytes).hexdigest(),
            "content_type": "application/json",
            "length": len(payload_bytes),
        }
        instruction_bytes = str(payload.get("instruction") or "").encode()
        instruction_path = controller_workspace / "commands" / "cmd_one" / "instruction.md"
        instruction_path.write_bytes(instruction_bytes)
        operation["payload"] = {
            "document_type": "invocation_input",
            "invocation_id": target["invocation_id"],
            "agent_spec_ref": "agent-spec://agent_one",
            "context_package_ref": "context-package://cmd_one",
            "instruction_ref": {
                "uri": instruction_path.resolve().as_uri(),
                "sha256": hashlib.sha256(instruction_bytes).hexdigest(),
                "content_type": "text/markdown",
                "length": len(instruction_bytes),
            },
            "completion_contract_ref": "completion-contract://tradecraft/default",
            "deadline": "2026-07-02T00:00:00Z",
            "extensions": {
                "tradecraft.dev/legacy_turn_request_ref": legacy_ref,
            },
        }
    else:
        operation["payload"] = payload
    validate_agent_runtime_contract("runtime-adapter-message.schema.json", operation)
    return operation


def _read_result_ref_payload(envelope: dict[str, object]) -> dict[str, object]:
    result_ref = cast(dict[str, object], envelope["result_ref"])
    uri = result_ref["uri"]
    assert isinstance(uri, str)
    parsed = urlparse(uri)
    assert parsed.scheme == "file"
    payload = json.loads(Path(unquote(parsed.path)).read_text(encoding="utf-8"))
    assert isinstance(payload, dict)
    return payload
