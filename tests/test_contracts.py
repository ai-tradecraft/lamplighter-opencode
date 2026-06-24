"""Contract validation tests."""

import pytest

from lamplighter_opencode.contracts.models import AgentSessionSpec, AgentTurnRequest
from lamplighter_opencode.contracts.validation import ContractValidationError, validate_contract


def test_agent_session_spec_validates() -> None:
    value = _valid_session_spec()

    validate_contract("agent_session_spec.schema.json", value)
    spec = AgentSessionSpec.from_dict(value)

    assert spec.agent_session_id == "session-1"
    assert spec.backend.provider_kind == "microsoft_foundry"


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


def _valid_session_spec() -> dict[str, object]:
    return {
        "agent_session_id": "session-1",
        "workspace_ref": "local://workspace",
        "context_package": {"goal": "test"},
        "backend": {
            "provider_kind": "microsoft_foundry",
            "model": "gpt-test",
            "base_url_env_var": "AZURE_OPENAI_BASE_URL",
            "api_key_env_var": "AZURE_OPENAI_API_KEY",
            "wire_api": "responses",
        },
        "tool_profile": {},
        "mcp_profile": {},
        "artifact_contract": {},
        "timeout_policy": {},
        "telemetry": {},
    }
