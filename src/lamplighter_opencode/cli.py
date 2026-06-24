"""Console script for lamplighter_opencode."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Annotated

import typer
from rich.console import Console

from lamplighter_opencode.contracts.models import AgentSessionSpec
from lamplighter_opencode.contracts.validation import ContractValidationError, validate_contract
from lamplighter_opencode.runtime.workspace import materialize_session_workspace

app = typer.Typer()
console = Console()

SpecPathArgument = Annotated[
    Path,
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
    spec_path: SpecPathArgument,
    runtime_root: RuntimeRootOption = Path(".agent-runtime"),
    skip_backend_env_check: SkipBackendEnvCheckOption = False,
) -> None:
    """Validate and materialize a local Lamplighter session workspace."""
    try:
        spec_value = _read_json_object(spec_path)
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

    console.print(f"Prepared Lamplighter session {spec.agent_session_id}")
    console.print(str(workspace.root))


def _read_json_object(path: Path) -> dict[str, object]:
    with path.open(encoding="utf-8") as input_file:
        value = json.load(input_file)
    if not isinstance(value, dict):
        msg = f"{path} must contain a JSON object"
        raise ContractValidationError(msg)
    return value


if __name__ == "__main__":
    app()
