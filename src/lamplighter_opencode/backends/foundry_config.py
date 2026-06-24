"""Microsoft Foundry backend configuration helpers."""

from __future__ import annotations

import os
from dataclasses import dataclass

from lamplighter_opencode.contracts.models import FoundryBackendConfig


@dataclass(frozen=True)
class MissingBackendEnvironmentError(RuntimeError):
    """Raised when a backend configuration references missing environment."""

    missing_variables: tuple[str, ...]

    def __str__(self) -> str:
        return f"Missing backend environment variables: {', '.join(self.missing_variables)}"


def validate_foundry_environment(config: FoundryBackendConfig, env: dict[str, str] | None = None) -> None:
    """Verify that the Foundry environment variable references are available."""
    source = os.environ if env is None else env
    missing = tuple(name for name in (config.base_url_env_var, config.api_key_env_var) if not source.get(name))
    if missing:
        raise MissingBackendEnvironmentError(missing)
