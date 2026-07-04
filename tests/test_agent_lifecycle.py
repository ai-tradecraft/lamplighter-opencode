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
    events = _read_adapter_events(controller_workspace)
    assert [event["event_type"] for event in events] == ["runtime.ready"]
    assert events[0]["sequence"] == 1
    assert events[0]["aggregate"] == {"type": "runtime", "id": "agent_one"}


def test_adapter_operation_ReplayedWithSameIdempotencyKey_ThenReturnsStoredResult(tmp_path: Path, monkeypatch) -> None:
    monkeypatch.delenv("CHAT_THROUGH_HARNESS_LOG_ROOT", raising=False)
    controller_workspace = tmp_path / "controller"
    operation_path = tmp_path / "operation.json"
    operation_path.write_text(
        json.dumps(
            _adapter_operation("StartRuntime", {"runtime_id": "agent_one"}, _agent_spec(), controller_workspace)
        ),
        encoding="utf-8",
    )
    start_calls = 0

    def fake_start_agent(layout, **kwargs):  # noqa: ANN001, ANN202, ARG001
        nonlocal start_calls
        start_calls += 1
        return {
            "status": "planned",
            "endpoint": "http://127.0.0.1:4097",
            "workspace": str(layout.workspace_dir),
        }

    monkeypatch.setattr("lamplighter_opencode.cli.start_agent", fake_start_agent)

    first = runner.invoke(app, ["adapter-operation", "--operation", str(operation_path), "--json"])
    second = runner.invoke(app, ["adapter-operation", "--operation", str(operation_path), "--json"])

    assert first.exit_code == 0, first.stdout
    assert second.exit_code == 0, second.stdout
    assert json.loads(second.stdout) == json.loads(first.stdout)
    assert start_calls == 1


def test_adapter_operation_ReadEvents_ThenReturnsFreshReplayBatch(tmp_path: Path, monkeypatch) -> None:
    monkeypatch.delenv("CHAT_THROUGH_HARNESS_LOG_ROOT", raising=False)
    controller_workspace = tmp_path / "controller"

    def fake_start_agent(layout, **kwargs):  # noqa: ANN001, ANN202, ARG001
        return {
            "status": "planned",
            "endpoint": "http://127.0.0.1:4097",
            "workspace": str(layout.workspace_dir),
        }

    monkeypatch.setattr("lamplighter_opencode.cli.start_agent", fake_start_agent)
    first_start = _adapter_operation(
        "StartRuntime",
        {"runtime_id": "agent_one"},
        _agent_spec("agent_one"),
        controller_workspace,
    )
    first_start_path = tmp_path / "start-agent-one.json"
    first_start_path.write_text(json.dumps(first_start), encoding="utf-8")
    started = runner.invoke(app, ["adapter-operation", "--operation", str(first_start_path), "--json"])
    assert started.exit_code == 0, started.stdout

    read_operation = _adapter_operation(
        "ReadEvents",
        {},
        {"document_type": "adapter_event_replay_request", "from_sequence": 1, "max_events": 10},
        controller_workspace,
    )
    read_operation["operation_id"] = "cmd_read_events"
    read_operation["idempotency_key"] = "read_events"
    read_path = tmp_path / "read-events.json"
    read_path.write_text(json.dumps(read_operation), encoding="utf-8")

    first_read = runner.invoke(app, ["adapter-operation", "--operation", str(read_path), "--json"])
    assert first_read.exit_code == 0, first_read.stdout
    first_batch = _read_result_ref_payload(json.loads(first_read.stdout))
    validate_agent_runtime_contract("runtime-resources.schema.json", first_batch)
    assert [event["event_type"] for event in cast(list[dict[str, object]], first_batch["events"])] == ["runtime.ready"]
    assert first_batch["next_sequence"] == 2

    second_start = _adapter_operation(
        "StartRuntime",
        {"runtime_id": "agent_two"},
        _agent_spec("agent_two"),
        controller_workspace,
    )
    second_start["operation_id"] = "cmd_two"
    second_start["idempotency_key"] = "idempotency_two"
    second_start_path = tmp_path / "start-agent-two.json"
    second_start_path.write_text(json.dumps(second_start), encoding="utf-8")
    started_again = runner.invoke(app, ["adapter-operation", "--operation", str(second_start_path), "--json"])
    assert started_again.exit_code == 0, started_again.stdout

    second_read = runner.invoke(app, ["adapter-operation", "--operation", str(read_path), "--json"])

    assert second_read.exit_code == 0, second_read.stdout
    second_batch = _read_result_ref_payload(json.loads(second_read.stdout))
    validate_agent_runtime_contract("runtime-resources.schema.json", second_batch)
    events = cast(list[dict[str, object]], second_batch["events"])
    assert [event["sequence"] for event in events] == [1, 2]
    assert second_batch["next_sequence"] == 3


def test_adapter_operation_ReusedIdempotencyKeyForDifferentOperation_ThenReportsConflict(tmp_path: Path) -> None:
    controller_workspace = tmp_path / "controller"
    first_path = tmp_path / "first-operation.json"
    second_path = tmp_path / "second-operation.json"
    first_path.write_text(
        json.dumps(_adapter_operation("InspectRuntime", {}, {}, controller_workspace)),
        encoding="utf-8",
    )
    second_operation = _adapter_operation(
        "ReadTranscript", {"runtime_id": "agent_one", "agent_session_id": "s1"}, {}, controller_workspace
    )
    second_path.write_text(json.dumps(second_operation), encoding="utf-8")

    first = runner.invoke(app, ["adapter-operation", "--operation", str(first_path), "--json"])
    second = runner.invoke(app, ["adapter-operation", "--operation", str(second_path), "--json"])

    assert first.exit_code == 0, first.stdout
    assert second.exit_code == 0, second.stdout
    envelope = json.loads(second.stdout)
    validate_agent_runtime_contract("runtime-adapter-message.schema.json", envelope)
    assert envelope["status"] == "failed"
    assert envelope["error"]["classification"] == "conflict"
    assert envelope["error"]["code"] == "idempotency_conflict"


def test_adapter_operation_UnsupportedOptionalOperation_ThenReportsUnsupportedCapability(tmp_path: Path) -> None:
    controller_workspace = tmp_path / "controller"
    operation_path = tmp_path / "operation.json"
    operation_path.write_text(
        json.dumps(
            _adapter_operation(
                "PauseInvocation", {"runtime_id": "agent_one", "invocation_id": "turn_one"}, {}, controller_workspace
            )
        ),
        encoding="utf-8",
    )

    result = runner.invoke(app, ["adapter-operation", "--operation", str(operation_path), "--json"])

    assert result.exit_code == 0, result.stdout
    envelope = json.loads(result.stdout)
    validate_agent_runtime_contract("runtime-adapter-message.schema.json", envelope)
    assert envelope["status"] == "failed"
    assert envelope["error"]["classification"] == "unsupported_capability"
    assert envelope["error"]["code"] == "unsupported_capability"


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


def test_adapter_operation_CreatesAndRestoresSnapshot_ThenReturnsResultRefs(tmp_path: Path, monkeypatch) -> None:
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
    (layout.workspace_dir / "notes.md").write_text("snapshot me", encoding="utf-8")
    create_path = tmp_path / "create-snapshot-operation.json"
    create_payload: dict[str, object] = {
        "document_type": "snapshot_request",
        "request_id": "snapshot_request_one",
        "target": {"controller_id": "controller_one", "runtime_id": "agent_one"},
        "purpose": "recovery_point",
        "requested_by": "controller",
        "reason": "test recovery point",
        "include": ["workspace", "agent_spec", "restore_recipe"],
        "requested_at": "2026-07-02T00:00:00Z",
    }
    create_operation = _adapter_operation(
        "CreateSnapshot",
        {"runtime_id": "agent_one"},
        create_payload,
        controller_workspace,
    )
    create_operation["operation_id"] = "cmd_snapshot_create"
    create_operation["idempotency_key"] = "snapshot_create_one"
    create_path.write_text(json.dumps(create_operation), encoding="utf-8")

    created = runner.invoke(app, ["adapter-operation", "--operation", str(create_path), "--json"])

    assert created.exit_code == 0, created.stdout
    created_envelope = json.loads(created.stdout)
    validate_agent_runtime_contract("runtime-adapter-message.schema.json", created_envelope)
    descriptor = _read_result_ref_payload(created_envelope)
    validate_agent_runtime_contract("runtime-resources.schema.json", descriptor)
    assert descriptor["document_type"] == "snapshot_descriptor"
    assert descriptor["resumability_mode"] == "manual"
    descriptor_extensions = cast(dict[str, object], descriptor["extensions"])
    local_descriptor_ref = cast(dict[str, object], descriptor_extensions["tradecraft.dev/local_descriptor_ref"])
    local_descriptor_uri = cast(str, local_descriptor_ref["uri"])
    descriptor_path = Path(unquote(urlparse(local_descriptor_uri).path))
    assert descriptor_path.exists()
    manifest_ref = cast(dict[str, object], descriptor["content_ref"])
    manifest_path = Path(unquote(urlparse(cast(str, manifest_ref["uri"])).path))
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    validate_agent_runtime_contract("runtime-resources.schema.json", manifest)
    events = _read_adapter_events(controller_workspace)
    assert [event["event_type"] for event in events] == ["snapshot.content_ready"]
    assert events[0]["aggregate"] == {"type": "snapshot", "id": descriptor["snapshot_id"]}

    restore_path = tmp_path / "restore-snapshot-operation.json"
    restore_operation = _adapter_operation(
        "RestoreSnapshot",
        {"runtime_id": "agent_one"},
        {"descriptor_ref": local_descriptor_ref, "restored_agent_id": "agent_restored"},
        controller_workspace,
    )
    restore_operation["operation_id"] = "cmd_snapshot_restore"
    restore_operation["idempotency_key"] = "snapshot_restore_one"
    restore_path.write_text(json.dumps(restore_operation), encoding="utf-8")

    restored = runner.invoke(app, ["adapter-operation", "--operation", str(restore_path), "--json"])

    assert restored.exit_code == 0, restored.stdout
    restored_envelope = json.loads(restored.stdout)
    validate_agent_runtime_contract("runtime-adapter-message.schema.json", restored_envelope)
    restore_payload = _read_result_ref_payload(restored_envelope)
    assert restore_payload["status"] == "restored"
    assert restore_payload["restored_agent_id"] == "agent_restored"
    restored_layout = agent_layout(controller_workspace, "agent_restored")
    assert (restored_layout.workspace_dir / "notes.md").read_text(encoding="utf-8") == "snapshot me"


def test_adapter_operation_CollectsSessionArtifacts_ThenReturnsArtifactManifest(tmp_path: Path, monkeypatch) -> None:
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
    create_agent_session(AgentChatSessionSpec.from_dict(_session_spec("session_one")), controller_workspace)
    artifact_path = agent_session_layout(controller_workspace, "agent_one", "session_one").artifacts_dir / "report.md"
    artifact_path.write_text("# Report\n", encoding="utf-8")
    operation = _adapter_operation(
        "CollectArtifacts",
        {"runtime_id": "agent_one", "agent_session_id": "session_one"},
        {},
        controller_workspace,
    )
    operation["operation_id"] = "cmd_collect_artifacts"
    operation["idempotency_key"] = "collect_artifacts_one"
    operation_path = tmp_path / "collect-artifacts.json"
    operation_path.write_text(json.dumps(operation), encoding="utf-8")

    result = runner.invoke(app, ["adapter-operation", "--operation", str(operation_path), "--json"])

    assert result.exit_code == 0, result.stdout
    envelope = json.loads(result.stdout)
    validate_agent_runtime_contract("runtime-adapter-message.schema.json", envelope)
    manifest = _read_result_ref_payload(envelope)
    validate_agent_runtime_contract("runtime-resources.schema.json", manifest)
    artifact = cast(list[dict[str, object]], manifest["artifacts"])[0]
    assert artifact["artifact_type"] == "artifact"
    content_ref = cast(dict[str, object], artifact["content_ref"])
    assert content_ref["uri"] == artifact_path.resolve().as_uri()


def test_adapter_operation_CollectsDiagnostics_ThenReturnsDiagnosticManifest(tmp_path: Path, monkeypatch) -> None:
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
    (layout.logs_dir / "adapter.log").write_text("diagnostic log\n", encoding="utf-8")
    operation = _adapter_operation(
        "CollectDiagnostics",
        {"runtime_id": "agent_one"},
        {},
        controller_workspace,
    )
    operation["operation_id"] = "cmd_collect_diagnostics"
    operation["idempotency_key"] = "collect_diagnostics_one"
    operation_path = tmp_path / "collect-diagnostics.json"
    operation_path.write_text(json.dumps(operation), encoding="utf-8")

    result = runner.invoke(app, ["adapter-operation", "--operation", str(operation_path), "--json"])

    assert result.exit_code == 0, result.stdout
    envelope = json.loads(result.stdout)
    validate_agent_runtime_contract("runtime-adapter-message.schema.json", envelope)
    manifest = _read_result_ref_payload(envelope)
    validate_agent_runtime_contract("runtime-resources.schema.json", manifest)
    artifacts = cast(list[dict[str, object]], manifest["artifacts"])
    assert artifacts
    assert {artifact["artifact_type"] for artifact in artifacts} == {"diagnostic"}


def test_adapter_operation_InteractionChannelLifecycle_ThenPersistsSessionAndMessages(tmp_path: Path) -> None:
    controller_workspace = tmp_path / "controller"
    content_path = tmp_path / "message.md"
    content_path.write_text("Here is the clarification.", encoding="utf-8")
    content_bytes = content_path.read_bytes()
    interaction_target: dict[str, object] = {
        "runtime_id": "agent_one",
        "agent_session_id": "session_one",
        "invocation_id": "turn_one",
        "interaction_session_id": "interaction_one",
    }
    open_payload: dict[str, object] = {
        "document_type": "interaction_session",
        "interaction_session_id": "interaction_one",
        "target": {"controller_id": "controller_one", **interaction_target},
        "mode": "synchronous",
        "purpose": "clarification",
        "status": "requested",
        "participants": [
            {
                "participant_ref": "agent://agent_one/session_one",
                "role": "agent",
                "joined_at": "2026-07-02T00:00:00Z",
            },
            {
                "participant_ref": "user://operator/test",
                "role": "operator",
                "joined_at": "2026-07-02T00:00:01Z",
            },
        ],
        "created_at": "2026-07-02T00:00:00Z",
        "updated_at": "2026-07-02T00:00:00Z",
    }
    message_payload: dict[str, object] = {
        "document_type": "interaction_message",
        "message_id": "message_one",
        "interaction_session_id": "interaction_one",
        "sequence": 1,
        "sender_ref": "user://operator/test",
        "content_ref": {
            "uri": content_path.resolve().as_uri(),
            "sha256": hashlib.sha256(content_bytes).hexdigest(),
            "content_type": "text/markdown",
            "length": len(content_bytes),
        },
        "sent_at": "2026-07-02T00:00:02Z",
    }
    operations = [
        (
            "open",
            _adapter_operation("OpenInteractionChannel", interaction_target, open_payload, controller_workspace),
        ),
        (
            "send",
            _adapter_operation("SendInteractionInput", interaction_target, message_payload, controller_workspace),
        ),
        (
            "ack",
            _adapter_operation(
                "AcknowledgeInteractionMessage",
                interaction_target,
                {"interaction_session_id": "interaction_one", "message_id": "message_one"},
                controller_workspace,
            ),
        ),
        (
            "close",
            _adapter_operation("CloseInteractionChannel", interaction_target, {}, controller_workspace),
        ),
    ]

    envelopes: list[dict[str, object]] = []
    for suffix, operation in operations:
        operation["operation_id"] = f"cmd_interaction_{suffix}"
        operation["idempotency_key"] = f"interaction_{suffix}"
        operation_path = tmp_path / f"interaction-{suffix}.json"
        operation_path.write_text(json.dumps(operation), encoding="utf-8")

        result = runner.invoke(app, ["adapter-operation", "--operation", str(operation_path), "--json"])

        assert result.exit_code == 0, result.stdout
        envelope = json.loads(result.stdout)
        validate_agent_runtime_contract("runtime-adapter-message.schema.json", envelope)
        envelopes.append(envelope)

    opened = _read_result_ref_payload(envelopes[0])
    sent = _read_result_ref_payload(envelopes[1])
    acknowledged = _read_result_ref_payload(envelopes[2])
    closed = _read_result_ref_payload(envelopes[3])
    validate_agent_runtime_contract("runtime-resources.schema.json", opened)
    validate_agent_runtime_contract("runtime-resources.schema.json", sent)
    validate_agent_runtime_contract("runtime-resources.schema.json", acknowledged)
    validate_agent_runtime_contract("runtime-resources.schema.json", closed)
    assert opened["status"] == "active"
    assert sent["message_id"] == "message_one"
    assert "acknowledged_at" in acknowledged
    assert closed["status"] == "closed"
    interaction_root = controller_workspace / "interactions" / "interaction_one"
    assert (interaction_root / "session.json").is_file()
    assert (interaction_root / "messages.jsonl").read_text(encoding="utf-8").count("message_one") == 1
    events = _read_adapter_events(controller_workspace)
    assert [event["event_type"] for event in events] == [
        "interaction.opened",
        "interaction.message",
        "interaction.acknowledged",
        "interaction.closed",
    ]
    assert [event["sequence"] for event in events] == [1, 2, 3, 4]
    message_event_payload = cast(dict[str, object], events[1]["payload"])
    assert message_event_payload["document_type"] == "interaction_message"


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


def _agent_spec(agent_id: str = "agent_one") -> dict[str, object]:
    return {
        "agent_id": agent_id,
        "workspace_ref": f"controller://agents/{agent_id}/workspace",
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


def _read_adapter_events(controller_workspace: Path) -> list[dict[str, object]]:
    journal_path = controller_workspace / "adapter-events" / "events.jsonl"
    events = [json.loads(line) for line in journal_path.read_text(encoding="utf-8").splitlines() if line]
    for event in events:
        validate_agent_runtime_contract("runtime-adapter-message.schema.json", event)
    return events
