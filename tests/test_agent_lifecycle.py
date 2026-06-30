"""Agent lifecycle command tests."""

import json
import os
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

from typer.testing import CliRunner

import lamplighter_opencode.runtime.workspace as workspace_module
from lamplighter_opencode.cli import app
from lamplighter_opencode.contracts.models import AgentChatSessionSpec
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
            "config": {
                "provider": "azure",
                "model": "azure/{env:AZURE_OPENAI_DEPLOYMENT}",
                "wire_api": "responses",
            },
            "required_env_vars": [],
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
