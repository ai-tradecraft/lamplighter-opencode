"""Opt-in end-to-end tests that hit the real OpenCode backend."""

from __future__ import annotations

import json
import os
import shutil

import pytest
from typer.testing import CliRunner

from lamplighter_opencode.cli import app
from lamplighter_opencode.runtime.workspace import deterministic_math_prompt, verify_azure_openai_api_key

runner = CliRunner()


pytestmark = pytest.mark.integration


def test_real_backend_returns_deterministic_math_result(tmp_path, monkeypatch) -> None:
    """Prepare a session and submit one real backend prompt through the CLI."""
    if os.environ.get("LAMPLIGHTER_OPENCODE_USE_REAL_BACKEND") != "1":
        pytest.skip("Set LAMPLIGHTER_OPENCODE_USE_REAL_BACKEND=1 to run the real backend test.")
    if shutil.which("opencode") is None:
        pytest.skip("The `opencode` executable is required for the real backend test.")

    monkeypatch.setenv("LAMPLIGHTER_OPENCODE_CONFIG_MODE", "project-only")

    response_id = verify_azure_openai_api_key()
    assert response_id.startswith("resp_")

    prompt, expected_answer = deterministic_math_prompt()
    session_id = "session_real_backend_e2e"
    spec_path = tmp_path / "spec.json"
    workspace = tmp_path / session_id / "workspace"
    request_path = workspace.parent / "request.json"

    spec_path.write_text(
        json.dumps(
            {
                "agent_session_id": session_id,
                "goal_run_id": "real_backend_e2e",
                "phase_run_id": "prompt_response",
                "agent_definition_id": "opencode.default",
                "workspace_ref": str(workspace),
                "repo_ref": "lamplighter-opencode",
                "branch_name": "poc-chat-through-harness",
            }
        ),
        encoding="utf-8",
    )
    prepare = runner.invoke(app, ["prepare-session", "--spec", str(spec_path), "--json"])
    assert prepare.exit_code == 0, prepare.stdout
    request_path.write_text(
        json.dumps(
            {
                "id": "turn_real_backend_e2e",
                "agent_session_id": session_id,
                "type": "prompt_response",
                "instruction": prompt,
            }
        ),
        encoding="utf-8",
    )

    submit = runner.invoke(app, ["submit-turn", "--session", session_id, "--request", str(request_path), "--json"])
    assert submit.exit_code == 0, submit.stdout

    result = json.loads(submit.stdout)
    assert result["status"] == "completed", result
    assert result["message"].strip() == expected_answer
