"""Typed Lamplighter contract models."""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import UTC, datetime
from typing import Any, Literal
from uuid import uuid4

JsonObject = dict[str, Any]


@dataclass(frozen=True)
class OpenCodeServerConfig:
    """OpenCode server binding for an agent session."""

    host: str = "127.0.0.1"
    port: int = 4096

    @classmethod
    def from_dict(cls, value: JsonObject) -> OpenCodeServerConfig:
        return cls(
            host=value.get("host", "127.0.0.1"),
            port=value.get("port", 4096),
        )

    def to_dict(self) -> JsonObject:
        return {
            "host": self.host,
            "port": self.port,
        }


@dataclass(frozen=True)
class OpenCodeBackendConfig:
    """OpenCode backend configuration referenced by an agent session."""

    kind: Literal["opencode"]
    server: OpenCodeServerConfig
    config: JsonObject
    required_env_vars: list[str] = field(default_factory=list)

    @classmethod
    def from_dict(cls, value: JsonObject) -> OpenCodeBackendConfig:
        return cls(
            kind=value["kind"],
            server=OpenCodeServerConfig.from_dict(value["server"]),
            config=value["config"],
            required_env_vars=value.get("required_env_vars", []),
        )

    def to_dict(self) -> JsonObject:
        return {
            "kind": self.kind,
            "server": self.server.to_dict(),
            "config": self.config,
            "required_env_vars": self.required_env_vars,
        }


@dataclass(frozen=True)
class AgentSpec:
    """Portable contract for one isolated OpenCode-backed agent."""

    agent_id: str
    workspace_ref: str
    backend: OpenCodeBackendConfig
    agent_definition_id: str | None = None
    repo_ref: str | None = None
    base_revision: str | None = None
    branch_name: str | None = None
    tool_profile: JsonObject = field(default_factory=dict)
    mcp_profile: JsonObject = field(default_factory=dict)
    telemetry: JsonObject = field(default_factory=dict)

    @classmethod
    def from_dict(cls, value: JsonObject) -> AgentSpec:
        return cls(
            agent_id=value["agent_id"],
            workspace_ref=value["workspace_ref"],
            backend=OpenCodeBackendConfig.from_dict(value["backend"]),
            agent_definition_id=value.get("agent_definition_id"),
            repo_ref=value.get("repo_ref"),
            base_revision=value.get("base_revision"),
            branch_name=value.get("branch_name"),
            tool_profile=value.get("tool_profile", {}),
            mcp_profile=value.get("mcp_profile", {}),
            telemetry=value.get("telemetry", {}),
        )

    def to_dict(self) -> JsonObject:
        return {
            "agent_id": self.agent_id,
            "workspace_ref": self.workspace_ref,
            "backend": self.backend.to_dict(),
            "agent_definition_id": self.agent_definition_id,
            "repo_ref": self.repo_ref,
            "base_revision": self.base_revision,
            "branch_name": self.branch_name,
            "tool_profile": self.tool_profile,
            "mcp_profile": self.mcp_profile,
            "telemetry": self.telemetry,
        }


@dataclass(frozen=True)
class AgentChatSessionSpec:
    """Portable contract for one conversation owned by an agent."""

    session_id: str
    agent_id: str
    context_package: JsonObject
    goal_run_id: str | None = None
    phase_run_id: str | None = None
    artifact_contract: JsonObject = field(default_factory=dict)
    timeout_policy: JsonObject = field(default_factory=dict)
    telemetry: JsonObject = field(default_factory=dict)

    @classmethod
    def from_dict(cls, value: JsonObject) -> AgentChatSessionSpec:
        return cls(
            session_id=value["session_id"],
            agent_id=value["agent_id"],
            context_package=value["context_package"],
            goal_run_id=value.get("goal_run_id"),
            phase_run_id=value.get("phase_run_id"),
            artifact_contract=value.get("artifact_contract", {}),
            timeout_policy=value.get("timeout_policy", {}),
            telemetry=value.get("telemetry", {}),
        )

    def to_dict(self) -> JsonObject:
        return {
            "session_id": self.session_id,
            "agent_id": self.agent_id,
            "context_package": self.context_package,
            "goal_run_id": self.goal_run_id,
            "phase_run_id": self.phase_run_id,
            "artifact_contract": self.artifact_contract,
            "timeout_policy": self.timeout_policy,
            "telemetry": self.telemetry,
        }


@dataclass(frozen=True)
class AgentSessionSpec:
    """Portable launch contract materialized by Lamplighter."""

    agent_session_id: str
    workspace_ref: str
    context_package: JsonObject
    backend: OpenCodeBackendConfig
    goal_run_id: str | None = None
    phase_run_id: str | None = None
    agent_definition_id: str | None = None
    resolved_agent_profile_id: str | None = None
    repo_ref: str | None = None
    base_revision: str | None = None
    branch_name: str | None = None
    tool_profile: JsonObject = field(default_factory=dict)
    mcp_profile: JsonObject = field(default_factory=dict)
    artifact_contract: JsonObject = field(default_factory=dict)
    timeout_policy: JsonObject = field(default_factory=dict)
    telemetry: JsonObject = field(default_factory=dict)

    @classmethod
    def from_dict(cls, value: JsonObject) -> AgentSessionSpec:
        if "context_package" not in value or "backend" not in value:
            value = {
                **value,
                "context_package": {
                    "goal": value.get("goal_run_id") or "chat_poc",
                    "phase": value.get("phase_run_id") or "prompt_response",
                },
                "backend": {
                    "kind": "opencode",
                    "server": {
                        "host": "127.0.0.1",
                        "port": 4096,
                    },
                    "config": {
                        "provider": "azure",
                        "model": "azure/{env:AZURE_OPENAI_DEPLOYMENT}",
                        "wire_api": "responses",
                    },
                    "required_env_vars": [],
                },
            }
        return cls(
            agent_session_id=value["agent_session_id"],
            workspace_ref=value["workspace_ref"],
            context_package=value["context_package"],
            backend=OpenCodeBackendConfig.from_dict(value["backend"]),
            goal_run_id=value.get("goal_run_id"),
            phase_run_id=value.get("phase_run_id"),
            agent_definition_id=value.get("agent_definition_id"),
            resolved_agent_profile_id=value.get("resolved_agent_profile_id"),
            repo_ref=value.get("repo_ref"),
            base_revision=value.get("base_revision"),
            branch_name=value.get("branch_name"),
            tool_profile=value.get("tool_profile", {}),
            mcp_profile=value.get("mcp_profile", {}),
            artifact_contract=value.get("artifact_contract", {}),
            timeout_policy=value.get("timeout_policy", {}),
            telemetry=value.get("telemetry", {}),
        )

    def to_dict(self) -> JsonObject:
        return {
            "agent_session_id": self.agent_session_id,
            "workspace_ref": self.workspace_ref,
            "context_package": self.context_package,
            "backend": self.backend.to_dict(),
            "goal_run_id": self.goal_run_id,
            "phase_run_id": self.phase_run_id,
            "agent_definition_id": self.agent_definition_id,
            "resolved_agent_profile_id": self.resolved_agent_profile_id,
            "repo_ref": self.repo_ref,
            "base_revision": self.base_revision,
            "branch_name": self.branch_name,
            "tool_profile": self.tool_profile,
            "mcp_profile": self.mcp_profile,
            "artifact_contract": self.artifact_contract,
            "timeout_policy": self.timeout_policy,
            "telemetry": self.telemetry,
        }


@dataclass(frozen=True)
class AgentTurnRequest:
    """One unit of agent work sent to a running session."""

    id: str
    agent_session_id: str
    type: Literal["prompt_response"]
    instruction: str
    created_at: str = field(default_factory=lambda: datetime.now(UTC).isoformat())
    input_artifact_refs: list[str] = field(default_factory=list)
    allowed_paths: list[str] = field(default_factory=list)
    expected_artifacts: list[str] = field(default_factory=list)
    correlation_id: str | None = None
    agent_id: str | None = None
    session_id: str | None = None
    timeout_policy: JsonObject = field(default_factory=dict)

    @classmethod
    def from_dict(cls, value: JsonObject) -> AgentTurnRequest:
        return cls(
            id=value["id"],
            agent_session_id=value["agent_session_id"],
            type=value["type"],
            instruction=value["instruction"],
            created_at=value.get("created_at", datetime.now(UTC).isoformat()),
            input_artifact_refs=value.get("input_artifact_refs", []),
            allowed_paths=value.get("allowed_paths", []),
            expected_artifacts=value.get("expected_artifacts", []),
            correlation_id=value.get("correlation_id"),
            agent_id=value.get("agent_id"),
            session_id=value.get("session_id"),
            timeout_policy=value.get("timeout_policy", {}),
        )

    def to_dict(self) -> JsonObject:
        return {
            "id": self.id,
            "agent_session_id": self.agent_session_id,
            "type": self.type,
            "created_at": self.created_at,
            "instruction": self.instruction,
            "input_artifact_refs": self.input_artifact_refs,
            "allowed_paths": self.allowed_paths,
            "expected_artifacts": self.expected_artifacts,
            "correlation_id": self.correlation_id,
            "agent_id": self.agent_id,
            "session_id": self.session_id,
            "timeout_policy": self.timeout_policy,
        }


@dataclass(frozen=True)
class AgentTurnResult:
    """Normalized result from an agent work request."""

    id: str
    agent_session_id: str
    request_id: str
    status: Literal["completed", "failed", "cancelled"]
    started_at: str
    ended_at: str
    message: str | None = None
    artifact_refs: list[str] = field(default_factory=list)
    changed_files: list[str] = field(default_factory=list)
    commands_observed: list[str] = field(default_factory=list)
    events_ref: str | None = None
    failure_report: JsonObject | None = None

    def to_dict(self) -> JsonObject:
        return {
            "id": self.id,
            "agent_session_id": self.agent_session_id,
            "request_id": self.request_id,
            "status": self.status,
            "started_at": self.started_at,
            "ended_at": self.ended_at,
            "message": self.message,
            "artifact_refs": self.artifact_refs,
            "changed_files": self.changed_files,
            "commands_observed": self.commands_observed,
            "events_ref": self.events_ref,
            "failure_report": self.failure_report,
        }


@dataclass(frozen=True)
class RuntimeEvent:
    """Append-only event emitted by Lamplighter while materializing a session."""

    event_type: str
    agent_session_id: str
    payload: JsonObject = field(default_factory=dict)
    event_id: str = field(default_factory=lambda: str(uuid4()))
    created_at: str = field(default_factory=lambda: datetime.now(UTC).isoformat())
    trace_id: str | None = None
    span_id: str | None = None
    correlation_id: str | None = None
    causation_id: str | None = None
    schema_version: str = "1.0"

    def to_dict(self) -> JsonObject:
        return {
            "event_id": self.event_id,
            "event_type": self.event_type,
            "agent_session_id": self.agent_session_id,
            "created_at": self.created_at,
            "trace_id": self.trace_id,
            "span_id": self.span_id,
            "correlation_id": self.correlation_id,
            "causation_id": self.causation_id,
            "schema_version": self.schema_version,
            "payload": self.payload,
        }
