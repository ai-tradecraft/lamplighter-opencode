"""Contract validation tests."""

import pytest

from lamplighter_opencode.contracts.models import AgentChatSessionSpec, AgentSessionSpec, AgentSpec, AgentTurnRequest
from lamplighter_opencode.contracts.validation import ContractValidationError, validate_contract


def test_agent_session_spec_validates() -> None:
    value = _valid_session_spec()

    validate_contract("agent_session_spec.schema.json", value)
    spec = AgentSessionSpec.from_dict(value)

    assert spec.agent_session_id == "session-1"
    assert spec.backend.kind == "opencode"
    assert spec.backend.config == {}


def test_agent_session_spec_accepts_adapter_private_backend_config() -> None:
    value = _valid_session_spec()
    value["backend"] = {
        "kind": "opencode",
        "server": {
            "host": "127.0.0.1",
            "port": 4096,
        },
        "config": {
            "provider": "fixture-provider",
            "model": "fixture/model",
            "wire_api": "responses",
        },
        "required_env_vars": ["FIXTURE_API_KEY"],
    }

    validate_contract("agent_session_spec.schema.json", value)
    spec = AgentSessionSpec.from_dict(value)

    assert spec.backend.config["provider"] == "fixture-provider"
    assert spec.backend.required_env_vars == ["FIXTURE_API_KEY"]


def test_agent_session_spec_rejects_extra_properties() -> None:
    value = _valid_session_spec()
    value["surprise"] = True

    with pytest.raises(ContractValidationError, match="Additional properties"):
        validate_contract("agent_session_spec.schema.json", value)


def test_agent_and_chat_session_have_distinct_identifiers() -> None:
    agent_value = {
        "agent_id": "agent_1",
        "workspace_ref": "local://agent-1/workspace",
        "backend": _valid_session_spec()["backend"],
        "tool_profile": {},
        "mcp_profile": {},
        "telemetry": {},
    }
    session_value = {
        "session_id": "session_1",
        "agent_id": "agent_1",
        "context_package": {"goal": "test"},
        "artifact_contract": {},
        "timeout_policy": {},
        "telemetry": {},
    }

    validate_contract("agent_spec.schema.json", agent_value)
    validate_contract("agent_chat_session_spec.schema.json", session_value)

    agent = AgentSpec.from_dict(agent_value)
    session = AgentChatSessionSpec.from_dict(session_value)
    assert agent.agent_id == session.agent_id
    assert session.session_id == "session_1"


def test_agent_contract_rejects_path_like_identifier() -> None:
    value = {
        "agent_id": "agent_../escape",
        "workspace_ref": "local://workspace",
        "backend": _valid_session_spec()["backend"],
    }

    with pytest.raises(ContractValidationError, match="does not match"):
        validate_contract("agent_spec.schema.json", value)


def test_agent_turn_request_is_named_for_agent_work() -> None:
    request = AgentTurnRequest(
        id="turn-1",
        agent_session_id="session-1",
        type="prompt_response",
        instruction="Summarize the workspace.",
        allowed_paths=["src/", "tests/"],
    )

    validate_contract("agent_turn_request.schema.json", request.to_dict())


def test_validate_contract_rejects_path_like_schema_names() -> None:
    with pytest.raises(ContractValidationError, match="Invalid schema name"):
        validate_contract("../agent_session_spec.schema.json", {})


def test_validate_contract_wraps_missing_schema_load_failures() -> None:
    with pytest.raises(ContractValidationError, match="Schema not found or unreadable"):
        validate_contract("missing.schema.json", {})


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
