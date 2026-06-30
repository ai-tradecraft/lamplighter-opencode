"""Controller, agent, and session layout tests."""

from pathlib import Path

import pytest

from lamplighter_opencode.runtime.layout import (
    agent_layout,
    agent_session_layout,
    materialize_agent_session_layout,
)


def test_agents_and_sessions_are_isolated(tmp_path: Path) -> None:
    first_agent = agent_layout(tmp_path, "agent_one")
    second_agent = agent_layout(tmp_path, "agent_two")
    first_session = agent_session_layout(tmp_path, "agent_one", "session_one")
    second_session = agent_session_layout(tmp_path, "agent_one", "session_two")

    assert first_agent.workspace_dir != second_agent.workspace_dir
    assert first_agent.runtime_dir != second_agent.runtime_dir
    assert first_session.root != second_session.root
    assert first_session.root.is_relative_to(first_agent.runtime_dir)
    assert second_session.root.is_relative_to(first_agent.runtime_dir)


def test_materializes_agent_and_session_directories(tmp_path: Path) -> None:
    session = materialize_agent_session_layout(tmp_path, "agent_one", "session_one")
    agent = agent_layout(tmp_path, "agent_one")

    assert agent.workspace_dir.is_dir()
    assert agent.logs_dir.is_dir()
    assert session.inbox_dir.is_dir()
    assert session.outbox_dir.is_dir()
    assert session.artifacts_dir.is_dir()


@pytest.mark.parametrize(
    ("agent_id", "session_id"),
    [
        ("../agent_escape", "session_one"),
        ("agent_one/child", "session_one"),
        ("agent_one", "../session_escape"),
        ("agent_one", "session_one/child"),
    ],
)
def test_rejects_path_like_identifiers(tmp_path: Path, agent_id: str, session_id: str) -> None:
    with pytest.raises(ValueError, match="Invalid"):
        agent_session_layout(tmp_path, agent_id, session_id)


def test_rejects_existing_symlink_that_escapes_controller(
    tmp_path: Path,
    tmp_path_factory: pytest.TempPathFactory,
) -> None:
    outside = tmp_path_factory.mktemp("outside")
    agents = tmp_path / "agents"
    agents.mkdir()
    (agents / "agent_escape").symlink_to(outside, target_is_directory=True)

    with pytest.raises(ValueError, match="escapes controller workspace"):
        agent_layout(tmp_path, "agent_escape")
