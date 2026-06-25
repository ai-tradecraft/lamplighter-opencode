"""Contract models used by the Lamplighter OpenCode CLI."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any


class ContractError(ValueError):
    """Raised when a JSON contract is invalid."""


def _required_str(data: dict[str, Any], key: str) -> str:
    value = data.get(key)
    if not isinstance(value, str) or not value:
        raise ContractError(f"`{key}` must be a non-empty string.")
    return value


def _optional_str(data: dict[str, Any], key: str) -> str | None:
    value = data.get(key)
    if value is None:
        return None
    if not isinstance(value, str):
        raise ContractError(f"`{key}` must be a string when provided.")
    return value


def _string_list(value: Any, key: str) -> list[str]:
    if value is None:
        return []
    if not isinstance(value, list) or any(not isinstance(item, str) for item in value):
        raise ContractError(f"`{key}` must be a list of strings.")
    return value


@dataclass(frozen=True)
class AgentSessionSpec:
    """Portable session launch contract."""

    agent_session_id: str
    goal_run_id: str
    phase_run_id: str
    agent_definition_id: str
    workspace_ref: str
    repo_ref: str
    branch_name: str
    backend: dict[str, Any] = field(default_factory=dict)
    communication_channel: dict[str, Any] = field(default_factory=dict)
    artifact_contract: dict[str, Any] = field(default_factory=dict)
    timeout_policy: dict[str, Any] = field(default_factory=dict)

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> AgentSessionSpec:
        if not isinstance(data, dict):
            raise ContractError("AgentSessionSpec must be an object.")
        return cls(
            agent_session_id=_required_str(data, "agent_session_id"),
            goal_run_id=_required_str(data, "goal_run_id"),
            phase_run_id=_required_str(data, "phase_run_id"),
            agent_definition_id=_required_str(data, "agent_definition_id"),
            workspace_ref=_required_str(data, "workspace_ref"),
            repo_ref=_required_str(data, "repo_ref"),
            branch_name=_required_str(data, "branch_name"),
            backend=dict(data.get("backend") or {}),
            communication_channel=dict(data.get("communication_channel") or {}),
            artifact_contract=dict(data.get("artifact_contract") or {}),
            timeout_policy=dict(data.get("timeout_policy") or {}),
        )

    def to_dict(self) -> dict[str, Any]:
        return {
            "agent_session_id": self.agent_session_id,
            "goal_run_id": self.goal_run_id,
            "phase_run_id": self.phase_run_id,
            "agent_definition_id": self.agent_definition_id,
            "workspace_ref": self.workspace_ref,
            "repo_ref": self.repo_ref,
            "branch_name": self.branch_name,
            "backend": self.backend,
            "communication_channel": self.communication_channel,
            "artifact_contract": self.artifact_contract,
            "timeout_policy": self.timeout_policy,
        }


@dataclass(frozen=True)
class AgentTurnRequest:
    """One unit of work sent to a prepared agent session."""

    id: str
    agent_session_id: str
    type: str
    instruction: str
    correlation_id: str | None = None

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> AgentTurnRequest:
        if not isinstance(data, dict):
            raise ContractError("AgentTurnRequest must be an object.")
        request_type = _required_str(data, "type")
        if request_type != "prompt_response":
            raise ContractError("Only `prompt_response` turn requests are supported.")
        return cls(
            id=_required_str(data, "id"),
            agent_session_id=_required_str(data, "agent_session_id"),
            type=request_type,
            instruction=_required_str(data, "instruction"),
            correlation_id=_optional_str(data, "correlation_id"),
        )

    def to_dict(self) -> dict[str, Any]:
        return {
            "id": self.id,
            "agent_session_id": self.agent_session_id,
            "type": self.type,
            "instruction": self.instruction,
            "correlation_id": self.correlation_id,
        }


@dataclass(frozen=True)
class FailureReport:
    """Structured failure details for a failed turn."""

    summary: str
    detail: str | None = None

    def to_dict(self) -> dict[str, Any]:
        return {"summary": self.summary, "detail": self.detail}


@dataclass(frozen=True)
class AgentTurnResult:
    """Normalized result for one agent turn."""

    id: str
    agent_session_id: str
    request_id: str
    status: str
    message: str
    artifact_refs: list[str] = field(default_factory=list)
    changed_files: list[str] = field(default_factory=list)
    commands_observed: list[str] = field(default_factory=list)
    failure_report: FailureReport | None = None

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> AgentTurnResult:
        if not isinstance(data, dict):
            raise ContractError("AgentTurnResult must be an object.")
        failure = data.get("failure_report")
        failure_report = None
        if isinstance(failure, dict):
            failure_report = FailureReport(
                summary=_required_str(failure, "summary"), detail=_optional_str(failure, "detail")
            )
        return cls(
            id=_required_str(data, "id"),
            agent_session_id=_required_str(data, "agent_session_id"),
            request_id=_required_str(data, "request_id"),
            status=_required_str(data, "status"),
            message=_required_str(data, "message"),
            artifact_refs=_string_list(data.get("artifact_refs"), "artifact_refs"),
            changed_files=_string_list(data.get("changed_files"), "changed_files"),
            commands_observed=_string_list(data.get("commands_observed"), "commands_observed"),
            failure_report=failure_report,
        )

    def to_dict(self) -> dict[str, Any]:
        return {
            "id": self.id,
            "agent_session_id": self.agent_session_id,
            "request_id": self.request_id,
            "status": self.status,
            "message": self.message,
            "artifact_refs": self.artifact_refs,
            "changed_files": self.changed_files,
            "commands_observed": self.commands_observed,
            "failure_report": self.failure_report.to_dict() if self.failure_report else None,
        }


@dataclass(frozen=True)
class RuntimeEvent:
    """Normalized event emitted by the harness."""

    id: str
    agent_session_id: str
    type: str
    payload: dict[str, Any]

    def to_dict(self) -> dict[str, Any]:
        return {
            "id": self.id,
            "agent_session_id": self.agent_session_id,
            "type": self.type,
            "payload": self.payload,
        }
