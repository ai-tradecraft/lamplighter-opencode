"""Contract validation tests."""

import pytest

from lamplighter_opencode.contracts.models import AgentSessionSpec, AgentTurnRequest
from lamplighter_opencode.contracts.validation import ContractValidationError, validate_contract


def test_agent_session_spec_validates() -> None:
    value = _valid_session_spec()

    validate_contract("agent_session_spec.schema.json", value)
    spec = AgentSessionSpec.from_dict(value)

    assert spec.agent_session_id == "session-1"
    assert spec.backend.kind == "opencode"
    assert spec.backend.config["provider"] == "azure"


def test_agent_session_spec_rejects_extra_properties() -> None:
    value = _valid_session_spec()
    value["surprise"] = True

    with pytest.raises(ContractValidationError, match="Additional properties"):
        validate_contract("agent_session_spec.schema.json", value)


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
            "config": {
                "provider": "azure",
                "model": "gpt-test",
                "wire_api": "responses",
            },
            "required_env_vars": ["AZURE_OPENAI_BASE_URL", "AZURE_OPENAI_API_KEY"],
        },
        "tool_profile": {},
        "mcp_profile": {},
        "artifact_contract": {},
        "timeout_policy": {},
        "telemetry": {},
    }
