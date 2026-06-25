"""Backend environment validation helpers."""

from __future__ import annotations

import os
from dataclasses import dataclass


@dataclass(frozen=True)
class MissingBackendEnvironmentError(RuntimeError):
    """Raised when a backend configuration references missing environment."""

    missing_variables: tuple[str, ...]

    def __str__(self) -> str:
        return f"Missing backend environment variables: {', '.join(self.missing_variables)}"


def validate_required_environment(required_env_vars: list[str], env: dict[str, str] | None = None) -> None:
    """Verify that all required backend environment variable references are available."""
    source = os.environ if env is None else env
    missing = tuple(name for name in required_env_vars if not source.get(name))
    if missing:
        raise MissingBackendEnvironmentError(missing)
