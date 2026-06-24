"""JSON Schema validation for Lamplighter contracts."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator

JsonObject = dict[str, Any]


class ContractValidationError(ValueError):
    """Raised when a Lamplighter contract fails schema validation."""


def validate_contract(schema_name: str, value: JsonObject) -> None:
    """Validate a JSON-like value against one of Lamplighter's bundled schemas."""
    schema = _load_schema(schema_name)
    validator = Draft202012Validator(schema)
    errors = sorted(validator.iter_errors(value), key=lambda error: list(error.absolute_path))
    if not errors:
        return

    messages = []
    for error in errors:
        location = ".".join(str(part) for part in error.absolute_path) or "<root>"
        messages.append(f"{location}: {error.message}")
    raise ContractValidationError("; ".join(messages))


def _load_schema(schema_name: str) -> JsonObject:
    schema_path = Path(__file__).parent / "schemas" / schema_name
    with schema_path.open(encoding="utf-8") as schema_file:
        loaded = json.load(schema_file)
    if not isinstance(loaded, dict):
        msg = f"Schema {schema_name} must contain a JSON object"
        raise ContractValidationError(msg)
    return loaded
