"""Console script for lamplighter_opencode."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Annotated

import typer
from rich.console import Console

from lamplighter_opencode.contracts.models import AgentSessionSpec, AgentTurnRequest
from lamplighter_opencode.contracts.validation import ContractValidationError, validate_contract
from lamplighter_opencode.runtime.workspace import materialize_session_workspace, start_opencode_server
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


@app.callback(invoke_without_command=True)
def main(ctx: typer.Context) -> None:
    """Print the Lamplighter for OpenCode welcome message when run with no subcommand."""
    if ctx.invoked_subcommand is not None:
        return
    console.print("Welcome to Lamplighter for OpenCode")


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
