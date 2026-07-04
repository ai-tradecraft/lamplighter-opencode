"""Runtime deployment-mode reporting for the OpenCode adapter."""

from __future__ import annotations

import os

DEPLOYMENT_MODE_ENV = "LAMPLIGHTER_ADAPTER_DEPLOYMENT_MODE"
SUPPORTED_DEPLOYMENT_MODES = ("local_process", "local_container")
DEFAULT_DEPLOYMENT_MODE = "local_process"


def current_deployment_mode() -> str:
    """Return the configured adapter deployment mode."""
    configured = os.environ.get(DEPLOYMENT_MODE_ENV, "").strip()
    if not configured:
        return DEFAULT_DEPLOYMENT_MODE
    if configured not in SUPPORTED_DEPLOYMENT_MODES:
        supported = ", ".join(SUPPORTED_DEPLOYMENT_MODES)
        msg = f"Unsupported {DEPLOYMENT_MODE_ENV}: {configured}. Expected one of: {supported}."
        raise ValueError(msg)
    return configured
