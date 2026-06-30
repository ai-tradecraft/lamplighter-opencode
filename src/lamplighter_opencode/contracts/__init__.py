"""Public contract models and validation helpers for Lamplighter."""

from lamplighter_opencode.contracts.models import (
    AgentChatHistory,
    AgentChatMessage,
    AgentChatSessionSpec,
    AgentSessionSpec,
    AgentSpec,
    AgentTurnRequest,
    AgentTurnResult,
    OpenCodeBackendConfig,
    OpenCodeServerConfig,
    RuntimeEvent,
)
from lamplighter_opencode.contracts.validation import ContractValidationError, validate_contract

__all__ = [
    "AgentChatHistory",
    "AgentChatMessage",
    "AgentChatSessionSpec",
    "AgentSpec",
    "AgentSessionSpec",
    "AgentTurnRequest",
    "AgentTurnResult",
    "ContractValidationError",
    "OpenCodeBackendConfig",
    "OpenCodeServerConfig",
    "RuntimeEvent",
    "validate_contract",
]
