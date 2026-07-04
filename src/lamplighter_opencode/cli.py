"""Console script for lamplighter_opencode."""

from __future__ import annotations

import hashlib
import json
from datetime import UTC, datetime
from pathlib import Path
from typing import Annotated, Any, cast
from urllib.parse import unquote, urlparse

import typer
from rich.console import Console

from lamplighter_opencode.contracts.models import (
    AgentChatSessionSpec,
    AgentSessionSpec,
    AgentSpec,
    AgentTurnRequest,
)
from lamplighter_opencode.contracts.validation import (
    ContractValidationError,
    validate_agent_runtime_contract,
    validate_contract,
)
from lamplighter_opencode.runtime.inventory import observe_runtime_inventory
from lamplighter_opencode.runtime.layout import agent_layout
from lamplighter_opencode.runtime.workspace import (
    cancel_agent_session,
    create_agent_session,
    create_agent_snapshot,
    get_agent_session_history,
    materialize_agent,
    materialize_session_workspace,
    restore_agent_snapshot,
    start_agent,
    start_opencode_server,
    stop_agent,
    stream_opencode_events,
    submit_agent_session_turn,
)
from lamplighter_opencode.runtime.workspace import submit_turn as submit_turn_request

app = typer.Typer()
console = Console()
ADAPTER_KIND = "opencode"
ADAPTER_VERSION = "0.1.0"
SUPPORTED_ADAPTER_OPERATIONS = frozenset(
    {
        "DescribeAdapter",
        "ValidateRuntimeSpec",
        "CheckReadiness",
        "InspectRuntime",
        "ReadEvents",
        "StartRuntime",
        "StopRuntime",
        "CreateSession",
        "StartInvocation",
        "CloseSession",
        "ReadTranscript",
        "CreateSnapshot",
        "RestoreSnapshot",
        "CollectArtifacts",
        "CollectDiagnostics",
        "OpenInteractionChannel",
        "SendInteractionInput",
        "AcknowledgeInteractionMessage",
        "CloseInteractionChannel",
    }
)

SpecPathArgument = Annotated[
    Path | None,
    typer.Argument(
        exists=True,
        readable=True,
        dir_okay=False,
        help="AgentSessionSpec JSON file.",
    ),
]
RuntimeRootOption = Annotated[
    Path,
    typer.Option(help="Directory where session runtime files are written."),
]
LegacySpecOption = Annotated[
    Path | None,
    typer.Option("--spec", exists=True, readable=True, dir_okay=False, help="AgentSessionSpec JSON file."),
]
JsonOutputOption = Annotated[
    bool,
    typer.Option("--json", help="Emit machine-readable JSON output."),
]
AdapterOperationOption = Annotated[
    Path,
    typer.Option("--operation", exists=True, readable=True, dir_okay=False, help="Adapter operation envelope JSON."),
]
SkipBackendEnvCheckOption = Annotated[
    bool,
    typer.Option(help="Skip backend environment variable presence checks. Intended for contract-only tests."),
]
ControllerWorkspaceOption = Annotated[
    Path,
    typer.Option(help="Controller-owned root containing isolated agent workspaces."),
]


class AdapterOperationError(RuntimeError):
    """Base error for adapter operation failures that map to protocol errors."""

    classification = "internal_adapter_error"
    code = "adapter_operation_failed"
    retryable = False


class UnsupportedAdapterOperationError(AdapterOperationError):
    """Raised when the adapter receives a known but unsupported protocol operation."""

    classification = "unsupported_capability"
    code = "unsupported_capability"


class IdempotencyConflictError(AdapterOperationError):
    """Raised when an idempotency key is reused for a different operation."""

    classification = "conflict"
    code = "idempotency_conflict"


@app.callback(invoke_without_command=True)
def main(ctx: typer.Context) -> None:
    """Print the Lamplighter for OpenCode welcome message when run with no subcommand."""
    if ctx.invoked_subcommand is not None:
        return
    console.print("Welcome to Lamplighter for OpenCode")


@app.command("prepare-agent")
def prepare_agent(
    spec: Annotated[Path, typer.Option(exists=True, readable=True, dir_okay=False)],
    controller_workspace: ControllerWorkspaceOption,
    skip_backend_env_check: SkipBackendEnvCheckOption = False,
    json_output: JsonOutputOption = False,
) -> None:
    """Validate and materialize one isolated agent workspace."""
    try:
        spec_value = _read_json_object(spec)
        validate_contract("agent_spec.schema.json", spec_value)
        agent_spec = AgentSpec.from_dict(spec_value)
        layout = materialize_agent(
            agent_spec,
            controller_workspace,
            validate_backend_environment=not skip_backend_env_check,
        )
    except (ContractValidationError, OSError, RuntimeError, ValueError, json.JSONDecodeError) as exc:
        console.print(f"[red]Failed to prepare agent:[/red] {exc}")
        raise typer.Exit(code=1) from exc

    result = {
        "agent_id": agent_spec.agent_id,
        "status": "allocated",
        "workspace_path": str(layout.workspace_dir),
        "runtime_path": str(layout.runtime_dir),
    }
    if json_output:
        typer.echo(json.dumps(result, indent=2))
        return
    console.print(f"Prepared Lamplighter agent {agent_spec.agent_id}")


@app.command("start-agent")
def start_agent_command(
    agent: Annotated[str, typer.Option()],
    controller_workspace: ControllerWorkspaceOption,
    host: Annotated[str, typer.Option(help="Host for the local OpenCode server.")] = "127.0.0.1",
    port: Annotated[int, typer.Option(help="Port for the local OpenCode server. Use 0 to choose a free port.")] = 0,
    dry_run: Annotated[bool, typer.Option(help="Write endpoint metadata without launching OpenCode.")] = False,
    json_output: JsonOutputOption = False,
) -> None:
    """Start the single supervised OpenCode server owned by an agent."""
    layout = agent_layout(controller_workspace, agent)
    if not layout.metadata_path.exists():
        console.print(f"[red]Failed to start agent:[/red] {agent} is not prepared.")
        raise typer.Exit(code=1)
    try:
        metadata = start_agent(layout, host=host, port=port, dry_run=dry_run)
    except (OSError, RuntimeError, TimeoutError, ValueError) as exc:
        console.print(f"[red]Failed to start agent:[/red] {exc}")
        raise typer.Exit(code=1) from exc
    if json_output:
        typer.echo(json.dumps({"agent_id": agent, **metadata}, indent=2))
        return
    console.print(f"Agent OpenCode server {metadata['status']} at {metadata['endpoint']}")


@app.command("stop-agent")
def stop_agent_command(
    agent: Annotated[str, typer.Option()],
    controller_workspace: ControllerWorkspaceOption,
    json_output: JsonOutputOption = False,
) -> None:
    """Stop an agent's OpenCode server without deleting its workspace."""
    layout = agent_layout(controller_workspace, agent)
    try:
        metadata = stop_agent(layout)
    except (OSError, RuntimeError, ValueError, json.JSONDecodeError) as exc:
        console.print(f"[red]Failed to stop agent:[/red] {exc}")
        raise typer.Exit(code=1) from exc
    if json_output:
        typer.echo(json.dumps({"agent_id": agent, **metadata}, indent=2))
        return
    console.print(f"Stopped Lamplighter agent {agent}")


@app.command("create-session")
def create_session_command(
    agent: Annotated[str, typer.Option()],
    spec: Annotated[Path, typer.Option(exists=True, readable=True, dir_okay=False)],
    controller_workspace: ControllerWorkspaceOption,
    json_output: JsonOutputOption = False,
) -> None:
    """Create one conversation within a prepared agent."""
    try:
        spec_value = _read_json_object(spec)
        validate_contract("agent_chat_session_spec.schema.json", spec_value)
        session_spec = AgentChatSessionSpec.from_dict(spec_value)
        if session_spec.agent_id != agent:
            raise ValueError("Session spec agent_id must match --agent.")
        layout = create_agent_session(session_spec, controller_workspace)
        metadata = _read_json_object(layout.metadata_path)
    except (ContractValidationError, OSError, RuntimeError, ValueError, json.JSONDecodeError) as exc:
        console.print(f"[red]Failed to create session:[/red] {exc}")
        raise typer.Exit(code=1) from exc
    if json_output:
        typer.echo(json.dumps(metadata, indent=2))
        return
    console.print(f"Created session {session_spec.session_id} in {agent}")


@app.command("cancel-session")
def cancel_session_command(
    agent: Annotated[str, typer.Option()],
    session: Annotated[str, typer.Option()],
    controller_workspace: ControllerWorkspaceOption,
    json_output: JsonOutputOption = False,
) -> None:
    """Cancel one conversation without stopping its agent."""
    try:
        metadata = cancel_agent_session(controller_workspace, agent, session)
    except (OSError, RuntimeError, ValueError, json.JSONDecodeError) as exc:
        console.print(f"[red]Failed to cancel session:[/red] {exc}")
        raise typer.Exit(code=1) from exc
    if json_output:
        typer.echo(json.dumps(metadata, indent=2))
        return
    console.print(f"Cancelled session {session}")


@app.command("create-snapshot")
def create_snapshot_command(
    agent: Annotated[str, typer.Option()],
    controller_workspace: ControllerWorkspaceOption,
    session: Annotated[str | None, typer.Option(help="Optional child session to include.")] = None,
    purpose: Annotated[str, typer.Option(help="Snapshot purpose, such as recovery or diagnostic.")] = "recovery",
    initiator: Annotated[str, typer.Option(help="Snapshot initiator identity or role.")] = "adapter",
    consistency: Annotated[str, typer.Option(help="Reported consistency level.")] = "crash-consistent",
    checkpoint_candidate: Annotated[
        bool, typer.Option(help="Whether this snapshot is nominated for checkpoint evaluation.")
    ] = False,
    json_output: JsonOutputOption = False,
) -> None:
    """Capture an agent workspace and runtime metadata as a local snapshot."""
    try:
        descriptor = create_agent_snapshot(
            controller_workspace,
            agent,
            session_id=session,
            purpose=purpose,
            initiator=initiator,
            consistency=consistency,
            checkpoint_candidate=checkpoint_candidate,
        )
    except (OSError, RuntimeError, ValueError, json.JSONDecodeError) as exc:
        console.print(f"[red]Failed to create snapshot:[/red] {exc}")
        raise typer.Exit(code=1) from exc
    if json_output:
        typer.echo(json.dumps(descriptor, indent=2))
        return
    console.print(f"Created snapshot {descriptor['snapshot_id']} for agent {agent}")


@app.command("restore-snapshot")
def restore_snapshot_command(
    snapshot: Annotated[
        Path, typer.Option(exists=True, readable=True, dir_okay=False, help="Snapshot descriptor JSON.")
    ],
    controller_workspace: ControllerWorkspaceOption,
    restored_agent: Annotated[str | None, typer.Option(help="Optional restored agent id.")] = None,
    json_output: JsonOutputOption = False,
) -> None:
    """Restore a local snapshot as an inspection-only agent workspace."""
    try:
        result = restore_agent_snapshot(
            snapshot,
            controller_workspace,
            restored_agent_id=restored_agent,
        )
    except (OSError, RuntimeError, ValueError, json.JSONDecodeError) as exc:
        console.print(f"[red]Failed to restore snapshot:[/red] {exc}")
        raise typer.Exit(code=1) from exc
    if json_output:
        typer.echo(json.dumps(result, indent=2))
        return
    console.print(f"Restored snapshot {result['snapshot_id']} as {result['restored_agent_id']}")


@app.command("get-session-history")
def get_session_history_command(
    agent: Annotated[str, typer.Option()],
    session: Annotated[str, typer.Option()],
    controller_workspace: ControllerWorkspaceOption,
    json_output: JsonOutputOption = False,
) -> None:
    """Read a session's authoritative conversation history from OpenCode."""
    try:
        history = get_agent_session_history(controller_workspace, agent, session)
        payload = history.to_dict()
        validate_contract("agent_chat_history.schema.json", payload)
    except (ContractValidationError, OSError, RuntimeError, ValueError, json.JSONDecodeError) as exc:
        console.print(f"[red]Failed to read session history:[/red] {exc}")
        raise typer.Exit(code=1) from exc
    if json_output:
        typer.echo(json.dumps(payload, indent=2))
        return
    console.print(f"Read {len(history.messages)} messages from session {session}")


@app.command("prepare-session")
def prepare_session(
    spec_path: SpecPathArgument = None,
    legacy_spec_path: LegacySpecOption = None,
    runtime_root: RuntimeRootOption = Path(".agent-runtime"),
    skip_backend_env_check: SkipBackendEnvCheckOption = False,
    json_output: JsonOutputOption = False,
) -> None:
    """Validate and materialize a local Lamplighter session workspace."""
    selected_spec_path = legacy_spec_path or spec_path
    if selected_spec_path is None:
        raise typer.BadParameter("A spec path is required.")

    try:
        raw_spec_value = _read_json_object(selected_spec_path)
        if legacy_spec_path is not None and runtime_root == Path(".agent-runtime"):
            runtime_root = _runtime_root_from_legacy_spec(raw_spec_value)
        spec_value = _normalize_session_spec(raw_spec_value)
        validate_contract("agent_session_spec.schema.json", spec_value)
        spec = AgentSessionSpec.from_dict(spec_value)
        workspace = materialize_session_workspace(
            spec,
            runtime_root,
            validate_backend_environment=not skip_backend_env_check,
        )
    except (ContractValidationError, OSError, RuntimeError, json.JSONDecodeError) as exc:
        console.print(f"[red]Failed to prepare session:[/red] {exc}")
        raise typer.Exit(code=1) from exc

    if json_output:
        typer.echo(
            json.dumps(
                {
                    "agent_session_id": spec.agent_session_id,
                    "status": "ready",
                    "workspace_ref": str(workspace.workspace_dir),
                    "artifact_root": str(workspace.artifacts_dir),
                    "log_path": str(workspace.logs_dir),
                },
                indent=2,
            )
        )
        return

    console.print(f"Prepared Lamplighter session {spec.agent_session_id}")
    console.print(str(workspace.root))


@app.command("submit-turn")
def submit_turn(
    session: Annotated[str, typer.Option()],
    request: Annotated[Path, typer.Option(exists=True, readable=True, dir_okay=False)],
    agent: Annotated[str | None, typer.Option()] = None,
    controller_workspace: Annotated[Path | None, typer.Option()] = None,
    json_output: JsonOutputOption = False,
) -> None:
    """Submit a prompt_response AgentTurnRequest to a prepared session."""
    try:
        request_value = _read_json_object(request)
        validate_contract("agent_turn_request.schema.json", request_value)
        turn_request = AgentTurnRequest.from_dict(request_value)
    except (ContractValidationError, OSError, json.JSONDecodeError, KeyError) as exc:
        console.print(f"[red]Failed to read turn request:[/red] {exc}")
        raise typer.Exit(code=1) from exc

    if turn_request.agent_session_id != session:
        raise typer.BadParameter("Request agent_session_id must match --session.")

    if agent is not None or controller_workspace is not None:
        if agent is None or controller_workspace is None:
            raise typer.BadParameter("--agent and --controller-workspace must be provided together.")
        result = submit_agent_session_turn(turn_request, controller_workspace, agent, session)
    else:
        result = submit_turn_request(turn_request, request.resolve().parent)
    if json_output:
        typer.echo(json.dumps(result.to_dict(), indent=2))
    else:
        console.print(result.message or result.status)


@app.command("start-session")
def start_session(
    session: Annotated[str, typer.Option()],
    runtime_root: RuntimeRootOption = Path(".agent-runtime"),
    host: Annotated[str, typer.Option(help="Host for the local OpenCode server.")] = "127.0.0.1",
    port: Annotated[int, typer.Option(help="Port for the local OpenCode server. Use 0 to choose a free port.")] = 0,
    dry_run: Annotated[bool, typer.Option(help="Write endpoint metadata without launching OpenCode.")] = False,
    json_output: JsonOutputOption = False,
) -> None:
    """Start or plan a supervised opencode serve process for a prepared session."""
    session_root = runtime_root / "sessions" / session
    if not session_root.exists():
        console.print(f"[red]Failed to start session:[/red] {session_root} does not exist.")
        raise typer.Exit(code=1)

    try:
        metadata = start_opencode_server(session_root, host=host, port=port, dry_run=dry_run)
    except (OSError, RuntimeError, TimeoutError, ValueError) as exc:
        console.print(f"[red]Failed to start session:[/red] {exc}")
        raise typer.Exit(code=1) from exc

    if json_output:
        typer.echo(json.dumps(metadata, indent=2))
        return

    console.print(f"OpenCode server {metadata['status']} at {metadata['endpoint']}")


@app.command("stream-events")
def stream_events(
    session: Annotated[str, typer.Option()],
    runtime_root: RuntimeRootOption = Path(".agent-runtime"),
    limit: Annotated[
        int, typer.Option(help="Stop after this many normalized events. Use 0 to stream until closed.")
    ] = 0,
    json_output: JsonOutputOption = False,
) -> None:
    """Subscribe to OpenCode SSE events and append normalized runtime events."""
    session_root = runtime_root / "sessions" / session
    if not session_root.exists():
        console.print(f"[red]Failed to stream events:[/red] {session_root} does not exist.")
        raise typer.Exit(code=1)

    try:
        events = stream_opencode_events(session_root, limit=limit or None)
    except (OSError, RuntimeError, ValueError, json.JSONDecodeError) as exc:
        console.print(f"[red]Failed to stream events:[/red] {exc}")
        raise typer.Exit(code=1) from exc

    if json_output:
        typer.echo(
            json.dumps(
                {
                    "agent_session_id": session,
                    "events_written": len(events),
                    "events": [event.to_dict() for event in events],
                },
                indent=2,
            )
        )
        return

    console.print(f"Wrote {len(events)} normalized OpenCode events")


@app.command("observe-runtimes")
def observe_runtimes(
    controller_workspace: ControllerWorkspaceOption,
    json_output: JsonOutputOption = False,
) -> None:
    """Observe OpenCode-backed runtime inventory for controller heartbeats."""
    inventory = observe_runtime_inventory(controller_workspace)
    if json_output:
        typer.echo(json.dumps(inventory, indent=2))
        return

    console.print(f"Observed {len(inventory['runtimes'])} OpenCode runtime(s).")


@app.command("adapter-operation")
def adapter_operation(
    operation: AdapterOperationOption,
    json_output: JsonOutputOption = False,
) -> None:
    """Execute one provider-neutral Agent Runtime Adapter operation envelope."""
    try:
        operation_value = _read_json_object(operation)
        validate_agent_runtime_contract("runtime-adapter-message.schema.json", operation_value)
    except (ContractValidationError, OSError, json.JSONDecodeError) as exc:
        console.print(f"[red]Failed to read adapter operation:[/red] {exc}")
        raise typer.Exit(code=1) from exc

    try:
        use_idempotent_result = _operation_uses_idempotent_result(operation_value)
        result = _replay_idempotent_result(operation_value) if use_idempotent_result else None
        if result is None:
            payload, content_type = _execute_adapter_operation(operation_value)
            result = _adapter_operation_result(operation_value, operation, payload, content_type)
            _record_adapter_event(operation_value, payload)
            if use_idempotent_result:
                _record_idempotent_result(operation_value, result)
    except (
        AdapterOperationError,
        ContractValidationError,
        OSError,
        RuntimeError,
        ValueError,
        json.JSONDecodeError,
        KeyError,
    ) as exc:
        result = _adapter_operation_failure(operation_value, exc)
    validate_agent_runtime_contract("runtime-adapter-message.schema.json", result)

    if json_output:
        typer.echo(json.dumps(result, indent=2))
        return

    if result["status"] == "completed":
        console.print(f"Completed adapter operation {operation_value.get('operation_id')}")
    else:
        error = result.get("error", {})
        error_summary = error.get("summary") if isinstance(error, dict) else None
        console.print(f"[red]Adapter operation failed:[/red] {error_summary or 'unknown failure'}")


def _execute_adapter_operation(operation: dict[str, object]) -> tuple[dict[str, Any], str]:
    operation_type = _operation_string(operation, "operation_type")
    target = _operation_object(operation, "target")
    extensions = _operation_object(operation, "extensions")
    controller_workspace = Path(_extension_string(extensions, "tradecraft.dev/controller_workspace"))

    if operation_type == "DescribeAdapter":
        return (_adapter_descriptor(), "application/vnd.tradecraft.adapter-descriptor+json")

    if operation_type == "ValidateRuntimeSpec":
        payload = operation.get("payload")
        if isinstance(payload, dict) and payload.get("message_type") == "adapter.resolved_runtime_spec":
            validate_agent_runtime_contract("runtime-adapter-message.schema.json", cast(dict[str, Any], payload))
        return (
            {
                "valid": True,
                "validated_at": _utc_now(),
                "adapter_kind": ADAPTER_KIND,
                "adapter_version": ADAPTER_VERSION,
                "diagnostics": [],
            },
            "application/vnd.tradecraft.runtime-spec-validation+json",
        )

    if operation_type == "CheckReadiness":
        return (
            {
                "status": "ready" if controller_workspace.exists() else "not_ready",
                "checked_at": _utc_now(),
                "controller_workspace": str(controller_workspace),
                "adapter_kind": ADAPTER_KIND,
                "adapter_version": ADAPTER_VERSION,
            },
            "application/vnd.tradecraft.adapter-readiness+json",
        )

    if operation_type == "InspectRuntime":
        return (observe_runtime_inventory(controller_workspace), "application/vnd.tradecraft.runtime-inventory+json")

    if operation_type == "ReadEvents":
        payload = _adapter_operation_payload(operation)
        validate_agent_runtime_contract("runtime-resources.schema.json", payload)
        return (
            _read_adapter_event_batch(controller_workspace, payload),
            "application/vnd.tradecraft.adapter-event-batch+json",
        )

    if operation_type == "StartRuntime":
        payload = _adapter_operation_payload(operation)
        validate_contract("agent_spec.schema.json", payload)
        agent_spec = AgentSpec.from_dict(payload)
        layout = materialize_agent(agent_spec, controller_workspace)
        metadata = start_agent(layout)
        return (
            {
                "agent_id": agent_spec.agent_id,
                "status": metadata.get("status") or "ready",
                "workspace_path": str(layout.workspace_dir),
                "runtime_path": str(layout.runtime_dir),
                **metadata,
            },
            "application/vnd.tradecraft.start-agent-result+json",
        )

    if operation_type == "StopRuntime":
        metadata = stop_agent(agent_layout(controller_workspace, _target_string(target, "runtime_id")))
        return (
            {
                "agent_id": _target_string(target, "runtime_id"),
                **metadata,
            },
            "application/vnd.tradecraft.stop-agent-result+json",
        )

    if operation_type == "CreateSession":
        payload = _adapter_operation_payload(operation)
        validate_contract("agent_chat_session_spec.schema.json", payload)
        session_spec = AgentChatSessionSpec.from_dict(payload)
        if session_spec.agent_id != _target_string(target, "runtime_id"):
            raise ValueError("Session spec agent_id must match target runtime_id.")
        layout = create_agent_session(session_spec, controller_workspace)
        return (_read_json_object(layout.metadata_path), "application/vnd.tradecraft.create-session-result+json")

    if operation_type == "StartInvocation":
        payload = _agent_turn_request_payload(operation)
        validate_contract("agent_turn_request.schema.json", payload)
        turn_request = AgentTurnRequest.from_dict(payload)
        session_id = _target_string(target, "agent_session_id")
        if turn_request.agent_session_id != session_id:
            raise ValueError("Turn request agent_session_id must match target agent_session_id.")
        result = submit_agent_session_turn(
            turn_request,
            controller_workspace,
            _target_string(target, "runtime_id"),
            session_id,
        )
        return (result.to_dict(), "application/vnd.tradecraft.agent-turn-result+json")

    if operation_type == "CloseSession":
        return (
            cancel_agent_session(
                controller_workspace,
                _target_string(target, "runtime_id"),
                _target_string(target, "agent_session_id"),
            ),
            "application/vnd.tradecraft.cancel-session-result+json",
        )

    if operation_type == "ReadTranscript":
        history = get_agent_session_history(
            controller_workspace,
            _target_string(target, "runtime_id"),
            _target_string(target, "agent_session_id"),
        )
        payload = history.to_dict()
        validate_contract("agent_chat_history.schema.json", payload)
        return (payload, "application/vnd.tradecraft.agent-chat-history+json")

    if operation_type == "OpenInteractionChannel":
        payload = _adapter_operation_payload(operation)
        validate_agent_runtime_contract("runtime-resources.schema.json", payload)
        return (
            _open_interaction_channel(controller_workspace, payload),
            "application/vnd.tradecraft.interaction-session+json",
        )

    if operation_type == "SendInteractionInput":
        payload = _adapter_operation_payload(operation)
        validate_agent_runtime_contract("runtime-resources.schema.json", payload)
        return (
            _send_interaction_input(controller_workspace, payload),
            "application/vnd.tradecraft.interaction-message+json",
        )

    if operation_type == "AcknowledgeInteractionMessage":
        payload = _adapter_operation_payload(operation)
        return (
            _acknowledge_interaction_message(controller_workspace, payload),
            "application/vnd.tradecraft.interaction-message+json",
        )

    if operation_type == "CloseInteractionChannel":
        return (
            _close_interaction_channel(controller_workspace, _target_string(target, "interaction_session_id")),
            "application/vnd.tradecraft.interaction-session+json",
        )

    if operation_type == "CreateSnapshot":
        payload = _adapter_operation_payload(operation)
        if payload.get("document_type") == "snapshot_request":
            validate_agent_runtime_contract("runtime-resources.schema.json", payload)
        descriptor = create_agent_snapshot(
            controller_workspace,
            _target_string(target, "runtime_id"),
            session_id=_optional_target_string(target, "agent_session_id"),
            purpose=_snapshot_purpose(payload),
            initiator=str(payload.get("requested_by") or "adapter"),
            consistency=_snapshot_consistency(payload),
            checkpoint_candidate=bool(payload.get("checkpoint_candidate", False)),
        )
        return (
            _canonical_snapshot_descriptor(descriptor, payload, target, operation),
            "application/vnd.tradecraft.snapshot-descriptor+json",
        )

    if operation_type == "RestoreSnapshot":
        payload = _adapter_operation_payload(operation)
        restored_agent = payload.get("restored_agent_id")
        if restored_agent is not None and not isinstance(restored_agent, str):
            raise ContractValidationError("RestoreSnapshot restored_agent_id must be a string when present.")
        return (
            restore_agent_snapshot(
                _snapshot_descriptor_path(payload),
                controller_workspace,
                restored_agent_id=restored_agent,
            ),
            "application/vnd.tradecraft.local-snapshot-restore-result+json",
        )

    if operation_type == "CollectArtifacts":
        return (
            _collect_artifact_manifest(controller_workspace, target, diagnostic=False),
            "application/vnd.tradecraft.artifact-manifest+json",
        )

    if operation_type == "CollectDiagnostics":
        return (
            _collect_artifact_manifest(controller_workspace, target, diagnostic=True),
            "application/vnd.tradecraft.artifact-manifest+json",
        )

    raise UnsupportedAdapterOperationError(f"Unsupported adapter operation: {operation_type}")


def _adapter_descriptor() -> dict[str, Any]:
    descriptor = {
        "message_type": "adapter.descriptor",
        "protocol_version": "1.0",
        "schema_version": "1.0",
        "adapter_kind": ADAPTER_KIND,
        "adapter_version": ADAPTER_VERSION,
        "deployment_modes": ["local_process"],
        "capabilities": {
            "persistent_runtime": True,
            "persistent_sessions": True,
            "concurrent_sessions": True,
            "streaming_output": False,
            "synchronous_interaction": True,
            "agent_initiated_interaction": False,
            "pause_resume": False,
            "snapshot_capture": True,
            "agent_suggested_snapshots": False,
            "exact_snapshot_restore": False,
            "reconstructed_restore": True,
            "snapshot_consistency_modes": ["crash_consistent"],
            "snapshot_transfer_profiles": ["local_content_handle"],
            "document_publication_source": False,
            "artifact_collection": True,
            "transcript_read": True,
            "transcript_source_ids": True,
        },
        "limits": {
            "max_concurrent_runtimes": 32,
            "max_concurrent_sessions_per_runtime": 32,
            "max_concurrent_invocations_per_runtime": 1,
        },
        "config_schema": "adapter-schema://opencode/1.0",
        "composition": {
            "backend": {"kind": "opencode", "version": "configured-locally"},
            "execution_environment": {"kind": "local_process", "version": "1.0"},
        },
    }
    validate_agent_runtime_contract("runtime-adapter-message.schema.json", descriptor)
    return descriptor


def _collect_artifact_manifest(
    controller_workspace: Path,
    target: dict[str, object],
    *,
    diagnostic: bool,
) -> dict[str, Any]:
    runtime_id = _target_string(target, "runtime_id")
    layout = agent_layout(controller_workspace, runtime_id)
    files = (
        _diagnostic_files(layout)
        if diagnostic
        else _artifact_files(layout, _optional_target_string(target, "agent_session_id"))
    )
    artifact_type = "diagnostic" if diagnostic else "artifact"
    manifest = {
        "document_type": "artifact_manifest",
        "manifest_id": f"artifact_manifest_{hashlib.sha256(f'{runtime_id}:{artifact_type}'.encode()).hexdigest()[:16]}",
        "created_at": _utc_now(),
        "artifacts": [
            _artifact_descriptor(layout.root, path, artifact_type=artifact_type)
            for path in sorted(files, key=lambda item: item.as_posix())
        ],
    }
    validate_agent_runtime_contract("runtime-resources.schema.json", manifest)
    return manifest


def _open_interaction_channel(controller_workspace: Path, payload: dict[str, Any]) -> dict[str, Any]:
    session = dict(payload)
    now = _utc_now()
    session["status"] = "active"
    session["updated_at"] = now
    session.setdefault("created_at", now)
    session.setdefault("last_controller_sequence", 0)
    session.setdefault("last_agent_sequence", 0)
    interaction_id = _interaction_id(session)
    root = _interaction_root(controller_workspace, interaction_id)
    root.mkdir(parents=True, exist_ok=True)
    _write_json(_interaction_session_path(controller_workspace, interaction_id), session)
    messages_path = _interaction_messages_path(controller_workspace, interaction_id)
    if not messages_path.exists():
        messages_path.write_text("", encoding="utf-8")
    validate_agent_runtime_contract("runtime-resources.schema.json", session)
    return session


def _send_interaction_input(controller_workspace: Path, payload: dict[str, Any]) -> dict[str, Any]:
    message = dict(payload)
    interaction_id = _interaction_id(message)
    session = _read_interaction_session(controller_workspace, interaction_id)
    sequence = int(message["sequence"])
    sender_ref = str(message["sender_ref"])
    if sender_ref.startswith("agent://"):
        session["last_agent_sequence"] = max(int(session.get("last_agent_sequence") or 0), sequence)
    else:
        session["last_controller_sequence"] = max(int(session.get("last_controller_sequence") or 0), sequence)
    session["status"] = "active"
    session["updated_at"] = _utc_now()
    _write_json(_interaction_session_path(controller_workspace, interaction_id), session)
    with _interaction_messages_path(controller_workspace, interaction_id).open("a", encoding="utf-8") as messages:
        messages.write(json.dumps(message, sort_keys=True))
        messages.write("\n")
    validate_agent_runtime_contract("runtime-resources.schema.json", message)
    return message


def _acknowledge_interaction_message(controller_workspace: Path, payload: dict[str, Any]) -> dict[str, Any]:
    interaction_id = str(payload.get("interaction_session_id") or "")
    message_id = str(payload.get("message_id") or "")
    if not interaction_id or not message_id:
        raise ContractValidationError(
            "AcknowledgeInteractionMessage requires interaction_session_id and message_id payload fields."
        )
    messages_path = _interaction_messages_path(controller_workspace, interaction_id)
    messages = _read_interaction_messages(messages_path)
    acknowledged_at = str(payload.get("acknowledged_at") or _utc_now())
    acknowledged: dict[str, Any] | None = None
    for message in messages:
        if message.get("message_id") == message_id:
            message["acknowledged_at"] = acknowledged_at
            acknowledged = message
            break
    if acknowledged is None:
        raise ContractValidationError(f"Interaction message not found: {message_id}")
    with messages_path.open("w", encoding="utf-8") as output:
        for message in messages:
            output.write(json.dumps(message, sort_keys=True))
            output.write("\n")
    session = _read_interaction_session(controller_workspace, interaction_id)
    session["updated_at"] = acknowledged_at
    _write_json(_interaction_session_path(controller_workspace, interaction_id), session)
    validate_agent_runtime_contract("runtime-resources.schema.json", acknowledged)
    return acknowledged


def _close_interaction_channel(controller_workspace: Path, interaction_id: str) -> dict[str, Any]:
    session = _read_interaction_session(controller_workspace, interaction_id)
    now = _utc_now()
    session["status"] = "closed"
    session["updated_at"] = now
    session["closed_at"] = now
    _write_json(_interaction_session_path(controller_workspace, interaction_id), session)
    validate_agent_runtime_contract("runtime-resources.schema.json", session)
    return session


def _interaction_id(payload: dict[str, Any]) -> str:
    interaction_id = payload.get("interaction_session_id")
    if not isinstance(interaction_id, str) or not interaction_id:
        raise ContractValidationError("Interaction payload must include interaction_session_id.")
    return interaction_id


def _interaction_root(controller_workspace: Path, interaction_id: str) -> Path:
    root = (controller_workspace / "interactions").expanduser().resolve()
    interaction_root = (root / _safe_identifier(interaction_id, "interaction_")).resolve()
    if not interaction_root.is_relative_to(root):
        raise ContractValidationError("Interaction path escapes controller workspace.")
    return interaction_root


def _interaction_session_path(controller_workspace: Path, interaction_id: str) -> Path:
    return _interaction_root(controller_workspace, interaction_id) / "session.json"


def _interaction_messages_path(controller_workspace: Path, interaction_id: str) -> Path:
    return _interaction_root(controller_workspace, interaction_id) / "messages.jsonl"


def _read_interaction_session(controller_workspace: Path, interaction_id: str) -> dict[str, Any]:
    path = _interaction_session_path(controller_workspace, interaction_id)
    if not path.is_file():
        raise ContractValidationError(f"Interaction session is not open: {interaction_id}")
    return cast(dict[str, Any], _read_json_object(path))


def _read_interaction_messages(path: Path) -> list[dict[str, Any]]:
    if not path.is_file():
        return []
    messages: list[dict[str, Any]] = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        value = json.loads(line)
        if not isinstance(value, dict):
            raise ContractValidationError("Interaction message journal entries must be JSON objects.")
        messages.append(cast(dict[str, Any], value))
    return messages


def _artifact_files(layout: Any, session_id: str | None) -> list[Path]:
    session_roots = [layout.sessions_dir / session_id] if session_id else sorted(layout.sessions_dir.glob("session_*"))
    files: list[Path] = []
    for session_root in session_roots:
        artifacts_dir = session_root / "artifacts"
        if artifacts_dir.is_dir():
            files.extend(path for path in artifacts_dir.rglob("*") if path.is_file())
    return files


def _diagnostic_files(layout: Any) -> list[Path]:
    candidates = [
        layout.metadata_path,
        layout.backend_config_path,
        layout.server_metadata_path,
    ]
    if layout.logs_dir.is_dir():
        candidates.extend(path for path in layout.logs_dir.rglob("*") if path.is_file())
    if layout.sessions_dir.is_dir():
        for session_root in sorted(layout.sessions_dir.glob("session_*")):
            candidates.extend(
                path
                for path in [
                    session_root / "session.json",
                    session_root / "context.json",
                    session_root / "events.jsonl",
                ]
                if path.is_file()
            )
    return [path for path in candidates if path.is_file()]


def _artifact_descriptor(root: Path, path: Path, *, artifact_type: str) -> dict[str, Any]:
    content = path.read_bytes()
    relative = path.resolve().relative_to(root.resolve())
    artifact_id = f"artifact_{_safe_identifier(relative.as_posix(), 'artifact_')}"
    return {
        "artifact_id": artifact_id,
        "artifact_type": artifact_type,
        "content_ref": {
            "uri": path.resolve().as_uri(),
            "sha256": hashlib.sha256(content).hexdigest(),
            "content_type": _content_type(path),
            "length": len(content),
        },
        "created_at": datetime.fromtimestamp(path.stat().st_mtime, UTC).isoformat().replace("+00:00", "Z"),
        "producer_ref": f"adapter://{ADAPTER_KIND}/{ADAPTER_VERSION}",
        "sensitivity": "internal",
    }


def _content_type(path: Path) -> str:
    if path.suffix == ".json":
        return "application/json"
    if path.suffix == ".jsonl":
        return "application/x-ndjson"
    if path.suffix in {".log", ".md", ".txt"}:
        return "text/plain"
    return "application/octet-stream"


def _adapter_operation_result(
    operation: dict[str, object],
    operation_path: Path,
    payload: dict[str, Any],
    content_type: str,
) -> dict[str, object]:
    result_ref = _write_adapter_operation_result_payload(operation_path, payload, content_type)
    return {
        "message_type": "adapter.operation_result",
        "protocol_version": _operation_string(operation, "protocol_version"),
        "schema_version": _operation_string(operation, "schema_version"),
        "result_id": f"result_{_operation_string(operation, 'operation_id')}",
        "operation_id": _operation_string(operation, "operation_id"),
        "status": "completed",
        "completed_at": _utc_now(),
        "fencing_token": _operation_int(operation, "fencing_token"),
        "correlation": _operation_object(operation, "correlation"),
        "result_ref": result_ref,
    }


def _record_adapter_event(operation: dict[str, object], result_payload: dict[str, Any]) -> None:
    event = _adapter_event_for_operation(operation, result_payload)
    if event is None:
        return

    extensions = _operation_object(operation, "extensions")
    controller_workspace = Path(_extension_string(extensions, "tradecraft.dev/controller_workspace"))
    event_root = controller_workspace / "adapter-events"
    event_root.mkdir(parents=True, exist_ok=True)
    sequence = _next_adapter_event_sequence(event_root)
    event["sequence"] = sequence
    event["event_id"] = f"event_{sequence}"
    validate_agent_runtime_contract("runtime-adapter-message.schema.json", event)
    journal_path = event_root / "events.jsonl"
    with journal_path.open("a", encoding="utf-8") as journal:
        journal.write(json.dumps(event, sort_keys=True))
        journal.write("\n")


def _operation_uses_idempotent_result(operation: dict[str, object]) -> bool:
    return _operation_string(operation, "operation_type") != "ReadEvents"


def _read_adapter_event_batch(controller_workspace: Path, request: dict[str, Any]) -> dict[str, Any]:
    from_sequence = _request_int(request, "from_sequence")
    max_events = _optional_request_int(request, "max_events") or 1000
    journal_path = controller_workspace / "adapter-events" / "events.jsonl"
    matching_events: list[dict[str, Any]] = []
    if journal_path.exists():
        for line in journal_path.read_text(encoding="utf-8").splitlines():
            if not line.strip():
                continue
            event = json.loads(line)
            if not isinstance(event, dict):
                raise ContractValidationError("Adapter event journal entries must be JSON objects.")
            validate_agent_runtime_contract("runtime-adapter-message.schema.json", event)
            sequence = _request_int(cast(dict[str, Any], event), "sequence")
            if sequence >= from_sequence:
                matching_events.append(cast(dict[str, Any], event))

    events = matching_events[:max_events]
    through_sequence = _request_int(events[-1], "sequence") if events else from_sequence - 1
    next_sequence = through_sequence + 1 if events else from_sequence
    batch = {
        "document_type": "adapter_event_batch",
        "adapter_kind": ADAPTER_KIND,
        "adapter_version": ADAPTER_VERSION,
        "from_sequence": from_sequence,
        "through_sequence": through_sequence,
        "next_sequence": next_sequence,
        "events": events,
        "exhausted": len(matching_events) <= max_events,
        "generated_at": _utc_now(),
    }
    validate_agent_runtime_contract("runtime-resources.schema.json", batch)
    return batch


def _next_adapter_event_sequence(event_root: Path) -> int:
    sequence_path = event_root / "sequence.txt"
    if not sequence_path.exists():
        sequence_path.write_text("1", encoding="utf-8")
        return 1
    current = int(sequence_path.read_text(encoding="utf-8"))
    sequence = current + 1
    sequence_path.write_text(str(sequence), encoding="utf-8")
    return sequence


def _adapter_event_for_operation(
    operation: dict[str, object],
    result_payload: dict[str, Any],
) -> dict[str, Any] | None:
    operation_type = _operation_string(operation, "operation_type")
    target = _operation_object(operation, "target")
    if operation_type == "StartRuntime":
        return _adapter_event(
            operation,
            event_type="runtime.ready",
            aggregate_type="runtime",
            aggregate_id=_target_string(target, "runtime_id"),
            target=target,
            payload=result_payload,
        )
    if operation_type == "StopRuntime":
        return _adapter_event(
            operation,
            event_type="runtime.stopped",
            aggregate_type="runtime",
            aggregate_id=_target_string(target, "runtime_id"),
            target=target,
            payload=result_payload,
        )
    if operation_type == "CreateSession":
        session_id = _result_string(result_payload, "session_id")
        return _adapter_event(
            operation,
            event_type="session.created",
            aggregate_type="agent_session",
            aggregate_id=session_id,
            target={**target, "agent_session_id": session_id},
            payload=result_payload,
        )
    if operation_type == "CloseSession":
        return _adapter_event(
            operation,
            event_type="session.closed",
            aggregate_type="agent_session",
            aggregate_id=_target_string(target, "agent_session_id"),
            target=target,
            payload=result_payload,
        )
    if operation_type == "StartInvocation":
        return _adapter_event(
            operation,
            event_type="invocation.outcome_reported",
            aggregate_type="invocation",
            aggregate_id=_target_string(target, "invocation_id"),
            target=target,
            payload=_invocation_outcome_payload(result_payload),
        )
    if operation_type == "CreateSnapshot":
        snapshot_id = _result_string(result_payload, "snapshot_id")
        return _adapter_event(
            operation,
            event_type="snapshot.content_ready",
            aggregate_type="snapshot",
            aggregate_id=snapshot_id,
            target={**target, "snapshot_id": snapshot_id},
            payload=result_payload,
        )
    if operation_type == "OpenInteractionChannel":
        interaction_id = _result_string(result_payload, "interaction_session_id")
        return _adapter_event(
            operation,
            event_type="interaction.opened",
            aggregate_type="interaction_session",
            aggregate_id=interaction_id,
            target={**target, "interaction_session_id": interaction_id},
            payload=result_payload,
        )
    if operation_type == "SendInteractionInput":
        return _adapter_event(
            operation,
            event_type="interaction.message",
            aggregate_type="interaction_session",
            aggregate_id=_result_string(result_payload, "interaction_session_id"),
            target=target,
            payload=result_payload,
        )
    if operation_type == "AcknowledgeInteractionMessage":
        return _adapter_event(
            operation,
            event_type="interaction.acknowledged",
            aggregate_type="interaction_session",
            aggregate_id=_result_string(result_payload, "interaction_session_id"),
            target=target,
            payload=result_payload,
        )
    if operation_type == "CloseInteractionChannel":
        return _adapter_event(
            operation,
            event_type="interaction.closed",
            aggregate_type="interaction_session",
            aggregate_id=_result_string(result_payload, "interaction_session_id"),
            target=target,
            payload=result_payload,
        )
    return None


def _adapter_event(
    operation: dict[str, object],
    *,
    event_type: str,
    aggregate_type: str,
    aggregate_id: str,
    target: dict[str, object],
    payload: dict[str, Any],
) -> dict[str, Any]:
    return {
        "message_type": "adapter.event",
        "protocol_version": _operation_string(operation, "protocol_version"),
        "schema_version": _operation_string(operation, "schema_version"),
        "event_id": "event_pending_sequence",
        "adapter_kind": ADAPTER_KIND,
        "adapter_version": ADAPTER_VERSION,
        "event_type": event_type,
        "aggregate": {
            "type": aggregate_type,
            "id": aggregate_id,
        },
        "sequence": 1,
        "occurred_at": _utc_now(),
        "payload_schema_version": _operation_string(operation, "schema_version"),
        "target": target,
        "correlation": _operation_object(operation, "correlation"),
        "payload": payload,
    }


def _invocation_outcome_payload(result_payload: dict[str, Any]) -> dict[str, Any]:
    status = _result_string(result_payload, "status")
    if status == "completed":
        kind = "completed"
    elif status == "cancelled":
        kind = "cancelled"
    else:
        kind = "non_retryable_failure"
    outcome: dict[str, Any] = {
        "kind": kind,
        "summary": str(result_payload.get("message") or f"Invocation {status}."),
        "confidence": 1,
        "extensions": {
            "tradecraft.dev/legacy_agent_turn_result": result_payload,
        },
    }
    failure_report = result_payload.get("failure_report")
    if isinstance(failure_report, dict):
        outcome["error"] = {
            "code": str(failure_report.get("code") or "agent_turn_failed"),
            "classification": str(failure_report.get("classification") or "provider_unavailable"),
            "summary": str(failure_report.get("summary") or outcome["summary"]),
            "retryable": bool(failure_report.get("retryable", False)),
        }
    return outcome


def _result_string(result_payload: dict[str, Any], key: str) -> str:
    value = result_payload[key]
    if not isinstance(value, str) or not value:
        raise ContractValidationError(f"Adapter operation result {key} must be a non-empty string.")
    return value


def _request_int(payload: dict[str, Any], key: str) -> int:
    value = payload[key]
    if not isinstance(value, int) or value < 0:
        raise ContractValidationError(f"Adapter operation request {key} must be a non-negative integer.")
    return value


def _optional_request_int(payload: dict[str, Any], key: str) -> int | None:
    value = payload.get(key)
    if value is None:
        return None
    if not isinstance(value, int) or value < 1:
        raise ContractValidationError(f"Adapter operation request {key} must be a positive integer when present.")
    return value


def _adapter_operation_failure(operation: dict[str, object], exc: Exception) -> dict[str, object]:
    try:
        protocol_version = _operation_string(operation, "protocol_version")
        schema_version = _operation_string(operation, "schema_version")
        operation_id = _operation_string(operation, "operation_id")
        fencing_token = _operation_int(operation, "fencing_token")
        correlation = _operation_object(operation, "correlation")
    except (ContractValidationError, KeyError, TypeError, ValueError):
        protocol_version = "1.0"
        schema_version = "1.0"
        operation_id = "unknown_operation"
        fencing_token = 1
        correlation = {"command_id": "unknown_command"}

    classification = "internal_adapter_error"
    code = "adapter_operation_failed"
    retryable = False
    if isinstance(exc, AdapterOperationError):
        classification = exc.classification
        code = exc.code
        retryable = exc.retryable
    elif isinstance(exc, ContractValidationError):
        classification = "invalid_request"
        code = "invalid_request"

    return {
        "message_type": "adapter.operation_result",
        "protocol_version": protocol_version,
        "schema_version": schema_version,
        "result_id": f"result_{operation_id}",
        "operation_id": operation_id,
        "status": "failed",
        "completed_at": _utc_now(),
        "fencing_token": fencing_token,
        "correlation": correlation,
        "error": {
            "code": code,
            "classification": classification,
            "summary": str(exc),
            "retryable": retryable,
        },
    }


def _adapter_operation_payload(operation: dict[str, object]) -> dict[str, Any]:
    payload = operation.get("payload")
    if isinstance(payload, dict):
        return cast(dict[str, Any], payload)

    payload_ref = operation.get("payload_ref")
    if isinstance(payload_ref, dict):
        uri = payload_ref.get("uri")
        if not isinstance(uri, str):
            raise ContractValidationError("Adapter operation payload_ref.uri must be a string.")
        parsed = urlparse(uri)
        if parsed.scheme != "file":
            raise ContractValidationError("Adapter operation payload_ref currently supports only file:// URIs.")
        payload_value = json.loads(Path(unquote(parsed.path)).read_text(encoding="utf-8"))
        if isinstance(payload_value, dict):
            return cast(dict[str, Any], payload_value)

    raise ContractValidationError("Adapter operation payload must be a JSON object or file payload_ref.")


def _agent_turn_request_payload(operation: dict[str, object]) -> dict[str, Any]:
    payload = _adapter_operation_payload(operation)
    if payload.get("document_type") != "invocation_input":
        return payload
    extensions = payload.get("extensions")
    if not isinstance(extensions, dict):
        raise ContractValidationError("invocation_input extensions must contain the legacy turn request reference.")
    legacy_ref = extensions.get("tradecraft.dev/legacy_turn_request_ref")
    if not isinstance(legacy_ref, dict):
        raise ContractValidationError("invocation_input must include tradecraft.dev/legacy_turn_request_ref.")
    uri = legacy_ref.get("uri")
    if not isinstance(uri, str):
        raise ContractValidationError("legacy turn request ref uri must be a string.")
    parsed = urlparse(uri)
    if parsed.scheme != "file":
        raise ContractValidationError("legacy turn request ref currently supports only file:// URIs.")
    legacy_payload = json.loads(Path(unquote(parsed.path)).read_text(encoding="utf-8"))
    if not isinstance(legacy_payload, dict):
        raise ContractValidationError("legacy turn request payload must be a JSON object.")
    return cast(dict[str, Any], legacy_payload)


def _snapshot_purpose(payload: dict[str, Any]) -> str:
    purpose = str(payload.get("purpose") or "recovery_point")
    if purpose == "checkpoint_candidate":
        return "checkpoint"
    if purpose == "recovery_point":
        return "recovery"
    return purpose.replace("_", "-")


def _snapshot_consistency(payload: dict[str, Any]) -> str:
    consistency = payload.get("consistency")
    if consistency in {"application_consistent", "quiesced", "crash_consistent"}:
        return str(consistency).replace("_", "-")
    return "crash-consistent"


def _canonical_snapshot_descriptor(
    local_descriptor: dict[str, Any],
    snapshot_request: dict[str, Any],
    target: dict[str, object],
    operation: dict[str, object],
) -> dict[str, Any]:
    descriptor_path = Path(str(local_descriptor["descriptor_path"])).expanduser().resolve()
    snapshot_root = descriptor_path.parent
    manifest_path = snapshot_root / str(
        cast(dict[str, Any], local_descriptor["manifest"]).get("path") or "manifest.json"
    )
    local_manifest = _read_json_object(manifest_path)
    canonical_manifest = _canonical_snapshot_manifest(snapshot_root, local_manifest)
    canonical_manifest_path = snapshot_root / "snapshot-manifest.v1.json"
    _write_json(canonical_manifest_path, canonical_manifest)
    validate_agent_runtime_contract("runtime-resources.schema.json", canonical_manifest)

    checkpoint_candidate = bool(
        snapshot_request.get("checkpoint_candidate", local_descriptor.get("checkpoint_candidate", False))
    )
    purpose = _canonical_snapshot_purpose(snapshot_request, checkpoint_candidate)
    descriptor = {
        "document_type": "snapshot_descriptor",
        "snapshot_id": str(local_descriptor["snapshot_id"]),
        "target": {
            **_operation_object(operation, "target"),
            "runtime_id": _target_string(target, "runtime_id"),
        },
        "created_at": str(local_descriptor["created_at"]),
        "purpose": purpose,
        "requested_by": _canonical_requested_by(snapshot_request),
        "trigger": str(snapshot_request.get("reason") or "adapter_snapshot_capture"),
        "consistency": _canonical_consistency(snapshot_request),
        "contents": _canonical_snapshot_contents(snapshot_request, canonical_manifest),
        "resumability_mode": "manual",
        "portable": False,
        "side_effect_state": "ambiguous",
        "retention": {
            "class": "candidate" if checkpoint_candidate else _retention_class(purpose),
        },
        "checkpoint_candidate": checkpoint_candidate,
        "content_ref": _content_reference(
            canonical_manifest_path,
            "application/vnd.tradecraft.snapshot-manifest+json",
            schema_ref="https://schemas.tradecraft.dev/agent-runtime/v1/runtime-resources.schema.json#/$defs/snapshot_manifest",
        ),
        "adapter_kind": ADAPTER_KIND,
        "adapter_version": ADAPTER_VERSION,
        "extensions": {
            "tradecraft.dev/local_snapshot_ref": str(local_descriptor.get("snapshot_ref") or ""),
            "tradecraft.dev/local_descriptor_ref": _content_reference(
                descriptor_path,
                "application/vnd.tradecraft.local-snapshot-descriptor+json",
            ),
            "tradecraft.dev/local_descriptor_path": str(descriptor_path),
        },
    }
    requested_by_ref = snapshot_request.get("requested_by_ref")
    if isinstance(requested_by_ref, str):
        descriptor["requested_by_ref"] = requested_by_ref
    validate_agent_runtime_contract("runtime-resources.schema.json", descriptor)
    return descriptor


def _canonical_snapshot_manifest(snapshot_root: Path, local_manifest: dict[str, object]) -> dict[str, Any]:
    components = local_manifest.get("components")
    if not isinstance(components, list):
        raise ContractValidationError("Local snapshot manifest must contain components.")
    canonical_components: list[dict[str, Any]] = []
    for component in components:
        if not isinstance(component, dict):
            raise ContractValidationError("Local snapshot component must be a JSON object.")
        path_value = component.get("path")
        if not isinstance(path_value, str):
            raise ContractValidationError("Local snapshot component path must be a string.")
        component_path = (snapshot_root / path_value).resolve()
        canonical_components.append(
            {
                "kind": _canonical_snapshot_component_kind(str(component.get("kind") or "")),
                "content_ref": _content_reference(component_path, _content_type(component_path)),
                "required_for_restore": bool(component.get("required_for_restoration", False)),
                "adapter_kind": ADAPTER_KIND,
                "adapter_version": ADAPTER_VERSION,
                "sensitivity": str(component.get("sensitivity") or "internal"),
            }
        )
    return {
        "document_type": "snapshot_manifest",
        "snapshot_id": str(local_manifest["snapshot_id"]),
        "created_at": str(local_manifest["created_at"]),
        "components": canonical_components,
        "extensions": {
            "tradecraft.dev/local_manifest_ref": _content_reference(
                snapshot_root / "manifest.json",
                "application/vnd.tradecraft.local-snapshot-manifest+json",
            )
        },
    }


def _canonical_snapshot_component_kind(local_kind: str) -> str:
    return {
        "workspace_filesystem_manifest": "workspace",
        "agent_metadata": "agent_spec",
        "backend_config": "environment",
        "provider_server_metadata": "provider_state",
        "agent_session_metadata": "agent_session",
    }.get(local_kind, "restore_recipe")


def _canonical_snapshot_purpose(snapshot_request: dict[str, Any], checkpoint_candidate: bool) -> str:
    purpose = snapshot_request.get("purpose")
    if purpose in {"recovery_point", "checkpoint_candidate", "diagnostic", "migration"}:
        return str(purpose)
    return "checkpoint_candidate" if checkpoint_candidate else "recovery_point"


def _canonical_requested_by(snapshot_request: dict[str, Any]) -> str:
    requested_by = snapshot_request.get("requested_by")
    if requested_by in {"agent", "orchestrator", "controller", "operator", "policy", "adapter"}:
        return str(requested_by)
    return "adapter"


def _canonical_consistency(snapshot_request: dict[str, Any]) -> str:
    consistency = snapshot_request.get("consistency")
    if consistency in {"application_consistent", "quiesced", "crash_consistent"}:
        return str(consistency)
    return "crash_consistent"


def _canonical_snapshot_contents(
    snapshot_request: dict[str, Any],
    canonical_manifest: dict[str, Any],
) -> list[str]:
    requested = snapshot_request.get("include")
    if isinstance(requested, list):
        contents = [item for item in requested if isinstance(item, str)]
        if contents:
            return sorted(set(contents))
    components = canonical_manifest.get("components")
    if isinstance(components, list):
        contents = [component.get("kind") for component in components if isinstance(component, dict)]
        values = [value for value in contents if isinstance(value, str)]
        if values:
            return sorted(set(values))
    return ["workspace", "restore_recipe"]


def _retention_class(purpose: str) -> str:
    if purpose == "checkpoint_candidate":
        return "candidate"
    if purpose == "diagnostic":
        return "diagnostic"
    return "rolling"


def _content_reference(path: Path, content_type: str, *, schema_ref: str | None = None) -> dict[str, object]:
    content = path.read_bytes()
    reference: dict[str, object] = {
        "uri": path.resolve().as_uri(),
        "sha256": hashlib.sha256(content).hexdigest(),
        "content_type": content_type,
        "length": len(content),
    }
    if schema_ref is not None:
        reference["schema_ref"] = schema_ref
    return reference


def _snapshot_descriptor_path(payload: dict[str, Any]) -> Path:
    if payload.get("document_type") == "snapshot_descriptor":
        extensions = payload.get("extensions")
        if isinstance(extensions, dict):
            local_path = extensions.get("tradecraft.dev/local_descriptor_path")
            if isinstance(local_path, str):
                return Path(local_path)
            local_ref = extensions.get("tradecraft.dev/local_descriptor_ref")
            if isinstance(local_ref, dict):
                uri = local_ref.get("uri")
                if isinstance(uri, str):
                    parsed = urlparse(uri)
                    if parsed.scheme == "file":
                        return Path(unquote(parsed.path))
    snapshot_value = payload.get("snapshot")
    if isinstance(snapshot_value, str):
        return Path(snapshot_value)
    descriptor_path = payload.get("descriptor_path")
    if isinstance(descriptor_path, str):
        return Path(descriptor_path)
    descriptor_ref = payload.get("descriptor_ref")
    if isinstance(descriptor_ref, dict):
        uri = descriptor_ref.get("uri")
        if isinstance(uri, str):
            parsed = urlparse(uri)
            if parsed.scheme == "file":
                return Path(unquote(parsed.path))
    raise ContractValidationError("RestoreSnapshot requires snapshot, descriptor_path, or file descriptor_ref.uri.")


def _replay_idempotent_result(operation: dict[str, object]) -> dict[str, object] | None:
    record_path = _idempotency_record_path(operation)
    if record_path is None or not record_path.is_file():
        return None
    record = _read_json_object(record_path)
    if record.get("fingerprint") != _operation_fingerprint(operation):
        raise IdempotencyConflictError("Idempotency key was reused for a different adapter operation.")
    result = record.get("result")
    if not isinstance(result, dict):
        raise ContractValidationError("Stored idempotent adapter result must be a JSON object.")
    return cast(dict[str, object], result)


def _record_idempotent_result(operation: dict[str, object], result: dict[str, object]) -> None:
    record_path = _idempotency_record_path(operation)
    if record_path is None:
        return
    record_path.parent.mkdir(parents=True, exist_ok=True)
    _write_json(
        record_path,
        {
            "idempotency_key": _operation_string(operation, "idempotency_key"),
            "operation_id": _operation_string(operation, "operation_id"),
            "fingerprint": _operation_fingerprint(operation),
            "recorded_at": _utc_now(),
            "result": result,
        },
    )


def _idempotency_record_path(operation: dict[str, object]) -> Path | None:
    try:
        extensions = _operation_object(operation, "extensions")
        controller_workspace = Path(_extension_string(extensions, "tradecraft.dev/controller_workspace"))
        key = _safe_identifier(_operation_string(operation, "idempotency_key"), "idem_")
    except (ContractValidationError, KeyError, TypeError, ValueError):
        return None
    return controller_workspace / "adapter-operations" / key / "result.json"


def _operation_fingerprint(operation: dict[str, object]) -> str:
    material = {
        "operation_type": operation.get("operation_type"),
        "target": operation.get("target"),
        "payload": operation.get("payload"),
        "payload_ref": operation.get("payload_ref"),
    }
    return hashlib.sha256(json.dumps(material, sort_keys=True, separators=(",", ":")).encode("utf-8")).hexdigest()


def _safe_identifier(value: str, fallback_prefix: str) -> str:
    safe = "".join(character if character.isalnum() or character in {"-", "_"} else "_" for character in value)
    safe = safe.strip("._-")
    return safe or f"{fallback_prefix}{hashlib.sha256(value.encode('utf-8')).hexdigest()[:16]}"


def _write_adapter_operation_result_payload(
    operation_path: Path,
    payload: dict[str, Any],
    content_type: str,
) -> dict[str, object]:
    payload_bytes = json.dumps(payload, indent=2).encode("utf-8")
    payload_path = operation_path.with_name(f"{operation_path.stem}-result-payload.json")
    payload_path.write_bytes(payload_bytes)
    return {
        "uri": payload_path.resolve().as_uri(),
        "sha256": hashlib.sha256(payload_bytes).hexdigest(),
        "content_type": content_type,
        "length": len(payload_bytes),
    }


def _write_json(path: Path, payload: dict[str, Any]) -> None:
    path.write_text(json.dumps(payload, indent=2), encoding="utf-8")


def _operation_string(operation: dict[str, object], key: str) -> str:
    value = operation[key]
    if not isinstance(value, str) or not value:
        raise ContractValidationError(f"Adapter operation {key} must be a non-empty string.")
    return value


def _extension_string(extensions: dict[str, object], key: str) -> str:
    value = extensions[key]
    if not isinstance(value, str) or not value:
        raise ContractValidationError(f"Adapter operation extension {key} must be a non-empty string.")
    return value


def _target_string(target: dict[str, object], key: str) -> str:
    value = target[key]
    if not isinstance(value, str) or not value:
        raise ContractValidationError(f"Adapter operation target {key} must be a non-empty string.")
    return value


def _optional_target_string(target: dict[str, object], key: str) -> str | None:
    value = target.get(key)
    if value is None:
        return None
    if not isinstance(value, str) or not value:
        raise ContractValidationError(f"Adapter operation target {key} must be a non-empty string when present.")
    return value


def _operation_int(operation: dict[str, object], key: str) -> int:
    value = operation[key]
    if not isinstance(value, int) or value < 1:
        raise ContractValidationError(f"Adapter operation {key} must be a positive integer.")
    return value


def _operation_object(operation: dict[str, object], key: str) -> dict[str, object]:
    value = operation[key]
    if not isinstance(value, dict):
        raise ContractValidationError(f"Adapter operation {key} must be a JSON object.")
    return cast(dict[str, object], value)


def _utc_now() -> str:
    return datetime.now(UTC).isoformat().replace("+00:00", "Z")


def _read_json_object(path: Path) -> dict[str, object]:
    with path.open(encoding="utf-8") as input_file:
        value = json.load(input_file)
    if not isinstance(value, dict):
        msg = f"{path} must contain a JSON object"
        raise ContractValidationError(msg)
    return value


def _normalize_session_spec(value: dict[str, object]) -> dict[str, object]:
    """Normalize the POC minimal spec into the richer foundation contract."""
    if "context_package" in value and "backend" in value:
        return value

    agent_session_id = str(value["agent_session_id"])
    branch_name = str(value.get("branch_name") or "poc-chat-through-harness")
    return {
        "agent_session_id": agent_session_id,
        "workspace_ref": value["workspace_ref"],
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
            "config": {},
            "required_env_vars": [],
        },
        "goal_run_id": value.get("goal_run_id"),
        "phase_run_id": value.get("phase_run_id"),
        "agent_definition_id": value.get("agent_definition_id"),
        "repo_ref": value.get("repo_ref"),
        "branch_name": branch_name,
        "tool_profile": {},
        "mcp_profile": {},
        "artifact_contract": value.get("artifact_contract") or {},
        "timeout_policy": value.get("timeout_policy") or {},
        "telemetry": {},
    }


def _runtime_root_from_legacy_spec(value: dict[str, object]) -> Path:
    workspace = Path(str(value["workspace_ref"])).expanduser().resolve()
    if workspace.parent.parent.name == "sessions":
        return workspace.parent.parent.parent
    return workspace.parent.parent


if __name__ == "__main__":
    app()
