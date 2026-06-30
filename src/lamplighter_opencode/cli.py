"""Console script for lamplighter_opencode."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Annotated

import typer
from rich.console import Console

from lamplighter_opencode.contracts.models import (
    AgentChatSessionSpec,
    AgentSessionSpec,
    AgentSpec,
    AgentTurnRequest,
)
from lamplighter_opencode.contracts.validation import ContractValidationError, validate_contract
from lamplighter_opencode.runtime.layout import agent_layout
from lamplighter_opencode.runtime.workspace import (
    cancel_agent_session,
    create_agent_session,
    get_agent_session_history,
    materialize_agent,
    materialize_session_workspace,
    start_agent,
    start_opencode_server,
    stop_agent,
    stream_opencode_events,
    submit_agent_session_turn,
)
from lamplighter_opencode.runtime.workspace import submit_turn as submit_turn_request

app = typer.Typer()
console = Console()

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
SkipBackendEnvCheckOption = Annotated[
    bool,
    typer.Option(help="Skip backend environment variable presence checks. Intended for contract-only tests."),
]
ControllerWorkspaceOption = Annotated[
    Path,
    typer.Option(help="Controller-owned root containing isolated agent workspaces."),
]


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
            "config": {
                "provider": "azure",
                "model": "azure/{env:AZURE_OPENAI_DEPLOYMENT}",
                "wire_api": "responses",
            },
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
