"""Tests for `lamplighter_opencode` package."""

import json

from typer.testing import CliRunner

import lamplighter_opencode
from lamplighter_opencode.cli import app
from lamplighter_opencode.contracts.models import AgentSessionSpec, AgentTurnRequest
from lamplighter_opencode.runtime.workspace import isolated_opencode_environment, materialize_opencode_config

runner = CliRunner()


def test_import() -> None:
    """Verify the package can be imported."""
    assert lamplighter_opencode


def test_cli_prints_welcome() -> None:
    """The CLI prints the Lamplighter welcome message."""
    result = runner.invoke(app)
    assert result.exit_code == 0
    assert "Welcome to Lamplighter for OpenCode" in result.stdout


def test_agent_session_spec_validation() -> None:
    """Session specs validate required launch fields."""
    spec = AgentSessionSpec.from_dict(
        {
            "agent_session_id": "session_1",
            "goal_run_id": "goal_1",
            "phase_run_id": "phase_1",
            "agent_definition_id": "opencode.default",
            "workspace_ref": "/tmp/session_1/workspace",
            "repo_ref": "lamplighter-opencode",
            "branch_name": "poc-chat-through-harness",
        }
    )

    assert spec.agent_session_id == "session_1"


def test_agent_turn_request_requires_prompt_response() -> None:
    """V1 only accepts prompt_response turns."""
    request = AgentTurnRequest.from_dict(
        {
            "id": "turn_1",
            "agent_session_id": "session_1",
            "type": "prompt_response",
            "instruction": "hello",
        }
    )

    assert request.instruction == "hello"


def test_prepare_session_command(tmp_path) -> None:
    """prepare-session writes the session workspace."""
    spec_path = tmp_path / "spec.json"
    workspace = tmp_path / "session_1" / "workspace"
    spec_path.write_text(
        json.dumps(
            {
                "agent_session_id": "session_1",
                "goal_run_id": "goal_1",
                "phase_run_id": "phase_1",
                "agent_definition_id": "opencode.default",
                "workspace_ref": str(workspace),
                "repo_ref": "lamplighter-opencode",
                "branch_name": "poc-chat-through-harness",
            }
        ),
        encoding="utf-8",
    )

    result = runner.invoke(app, ["prepare-session", "--spec", str(spec_path), "--json"])

    assert result.exit_code == 0
    assert workspace.exists()
    assert (workspace.parent / "session.json").exists()


def test_submit_turn_command_returns_fake_result(tmp_path) -> None:
    """submit-turn returns a normalized fake result by default."""
    request_path = tmp_path / "turn.json"
    request_path.write_text(
        json.dumps(
            {
                "id": "turn_1",
                "agent_session_id": "session_1",
                "type": "prompt_response",
                "instruction": "hello",
            }
        ),
        encoding="utf-8",
    )

    result = runner.invoke(app, ["submit-turn", "--session", "session_1", "--request", str(request_path), "--json"])

    assert result.exit_code == 0
    assert "Fake OpenCode response" in result.stdout


def test_opencode_environment_is_session_local(tmp_path) -> None:
    """Real backend execution does not inherit the user's global OpenCode home."""
    root = tmp_path / "session_1"
    (root / "workspace").mkdir(parents=True)

    config_path = materialize_opencode_config(root)
    env = isolated_opencode_environment(root)

    assert config_path == root / "workspace" / "opencode.json"
    assert env["HOME"] == str(root / "home")
    assert env["XDG_CONFIG_HOME"] == str(root / "xdg-config")
    assert env["XDG_DATA_HOME"] == str(root / "xdg-data")
    assert env["XDG_CACHE_HOME"] == str(root / "xdg-cache")
    assert env["OPENCODE_CONFIG"] == str(config_path)
    assert env["OPENCODE_CONFIG_DIR"] == str(root / "opencode-config")
