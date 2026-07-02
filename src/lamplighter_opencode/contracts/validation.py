"""JSON Schema validation for Lamplighter contracts."""

from __future__ import annotations

import json
import os
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator

JsonObject = dict[str, Any]
SCHEMAS_RELATIVE_PATH = Path("contracts/lamplighter-opencode/schemas")


class ContractValidationError(ValueError):
    """Raised when a Lamplighter contract fails schema validation."""


def validate_contract(schema_name: str, value: JsonObject) -> None:
    """Validate a JSON-like value against one of Lamplighter's shared schemas."""
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
    if schema_name in {".", ".."} or "/" in schema_name or "\\" in schema_name:
        msg = f"Invalid schema name: {schema_name}"
        raise ContractValidationError(msg)

    schema_path = _schemas_dir() / schema_name
    try:
        with schema_path.open(encoding="utf-8") as schema_file:
            loaded = json.load(schema_file)
    except OSError as exc:
        msg = f"Schema not found or unreadable: {schema_name}"
        raise ContractValidationError(msg) from exc
    except json.JSONDecodeError as exc:
        msg = f"Schema {schema_name} must contain valid JSON"
        raise ContractValidationError(msg) from exc

    if not isinstance(loaded, dict):
        msg = f"Schema {schema_name} must contain a JSON object"
        raise ContractValidationError(msg)
    return loaded


def _schemas_dir() -> Path:
    contracts_root = os.environ.get("TRADECRAFT_CONTRACTS_ROOT")
    if contracts_root:
        return Path(contracts_root) / SCHEMAS_RELATIVE_PATH

    for ancestor in Path(__file__).resolve().parents:
        candidate = ancestor / "tradecraft-contracts" / SCHEMAS_RELATIVE_PATH
        if candidate.is_dir():
            return candidate

    return Path(__file__).parent / "schemas"
