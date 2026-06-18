"""Tests for `lamplighter_opencode` package."""

from typer.testing import CliRunner

import lamplighter_opencode
from lamplighter_opencode.cli import app

runner = CliRunner()


def test_import() -> None:
    """Verify the package can be imported."""
    assert lamplighter_opencode


def test_cli_prints_welcome() -> None:
    """The CLI prints the Lamplighter welcome message."""
    result = runner.invoke(app)
    assert result.exit_code == 0
    assert "Welcome to Lamplighter for OpenCode" in result.stdout
