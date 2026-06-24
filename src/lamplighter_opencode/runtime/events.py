"""Runtime event persistence."""

from __future__ import annotations

import json
from pathlib import Path

from lamplighter_opencode.contracts.models import RuntimeEvent
from lamplighter_opencode.contracts.validation import validate_contract


def append_runtime_event(path: Path, event: RuntimeEvent) -> None:
    """Append a normalized runtime event to a JSON Lines file."""
    value = event.to_dict()
    validate_contract("runtime_event.schema.json", value)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8") as event_file:
        event_file.write(json.dumps(value, sort_keys=True))
        event_file.write("\n")
