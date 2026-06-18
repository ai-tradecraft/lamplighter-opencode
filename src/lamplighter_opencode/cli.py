"""Console script for lamplighter_opencode."""

import typer
from rich.console import Console

app = typer.Typer()
console = Console()


@app.command()
def main() -> None:
    """Print the Lamplighter for OpenCode welcome message."""
    console.print("Welcome to Lamplighter for OpenCode")


if __name__ == "__main__":
    app()
