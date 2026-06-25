"""Console script for lamplighter_opencode."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Annotated

import typer
from rich.console import Console

from lamplighter_opencode.contracts.io import read_json
from lamplighter_opencode.contracts.models import AgentSessionSpec, AgentTurnRequest, ContractError
from lamplighter_opencode.runtime.workspace import prepare_session as prepare_session_workspace
from lamplighter_opencode.runtime.workspace import submit_turn as submit_turn_request

app = typer.Typer()
console = Console()


@app.callback(invoke_without_command=True)
def main(ctx: typer.Context) -> None:
    """Print the Lamplighter for OpenCode welcome message when run with no subcommand."""
    if ctx.invoked_subcommand is not None:
        return
    console.print("Welcome to Lamplighter for OpenCode")


@app.command("prepare-session")
def prepare_session(
    spec: Annotated[Path, typer.Option(exists=True, readable=True)],
    json_output: Annotated[bool, typer.Option("--json")] = False,
) -> None:
    """Validate and materialize an AgentSessionSpec."""
    try:
        session_spec = AgentSessionSpec.from_dict(read_json(spec))
        result = prepare_session_workspace(session_spec)
    except (OSError, ValueError, ContractError) as exc:
        raise typer.BadParameter(str(exc)) from exc

    if json_output:
        typer.echo(json.dumps(result, indent=2))
    else:
        console.print(f"Prepared session {result['agent_session_id']}")


@app.command("submit-turn")
def submit_turn(
    session: Annotated[str, typer.Option()],
    request: Annotated[Path, typer.Option(exists=True, readable=True)],
    json_output: Annotated[bool, typer.Option("--json")] = False,
) -> None:
    """Submit a prompt_response AgentTurnRequest to a prepared session."""
    try:
        turn_request = AgentTurnRequest.from_dict(read_json(request))
    except (OSError, ValueError, ContractError) as exc:
        raise typer.BadParameter(str(exc)) from exc

    if turn_request.agent_session_id != session:
        raise typer.BadParameter("Request agent_session_id must match --session.")

    root = request.resolve().parent
    result = submit_turn_request(turn_request, root)
    if json_output:
        typer.echo(json.dumps(result.to_dict(), indent=2))
    else:
        console.print(result.message)


if __name__ == "__main__":
    app()
