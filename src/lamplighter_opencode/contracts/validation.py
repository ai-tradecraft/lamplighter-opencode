"""JSON Schema validation for Lamplighter contracts."""

from __future__ import annotations

import json
import os
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator
from referencing import Registry, Resource
from referencing.jsonschema import DRAFT202012

JsonObject = dict[str, Any]
SCHEMAS_RELATIVE_PATH = Path("contracts/lamplighter-opencode/schemas")
AGENT_RUNTIME_SCHEMAS_RELATIVE_PATH = Path("contracts/agent-runtime/v1/schemas")


class ContractValidationError(ValueError):
    """Raised when a Lamplighter contract fails schema validation."""


def validate_contract(schema_name: str, value: JsonObject) -> None:
    """Validate a JSON-like value against one of Lamplighter's shared schemas."""
    schema_path = _schema_path(_schemas_dir(), schema_name)
    schema = _load_schema(schema_path, schema_name)
    validator = Draft202012Validator(schema)
    _raise_validation_errors(validator, value)


def validate_agent_runtime_contract(schema_name: str, value: JsonObject) -> None:
    """Validate a JSON-like value against the shared Agent Runtime schemas."""
    schemas_dir = _agent_runtime_schemas_dir()
    schema_path = _schema_path(schemas_dir, schema_name)
    schema = _load_schema(schema_path, schema_name)
    validator = Draft202012Validator(schema, registry=_schema_registry(schemas_dir))
    _raise_validation_errors(validator, value)


def _raise_validation_errors(validator: Any, value: JsonObject) -> None:
    errors = sorted(validator.iter_errors(value), key=lambda error: list(error.absolute_path))
    if not errors:
        return

    messages = []
    for error in errors:
        location = ".".join(str(part) for part in error.absolute_path) or "<root>"
        messages.append(f"{location}: {error.message}")
    raise ContractValidationError("; ".join(messages))


def _schema_path(schemas_dir: Path, schema_name: str) -> Path:
    if schema_name in {".", ".."} or "/" in schema_name or "\\" in schema_name:
        msg = f"Invalid schema name: {schema_name}"
        raise ContractValidationError(msg)

    return schemas_dir / schema_name


def _load_schema(schema_path: Path, schema_name: str) -> JsonObject:
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


def _schema_registry(schemas_dir: Path) -> Registry:
    resources: list[tuple[str, Resource[JsonObject]]] = []
    for schema_path in schemas_dir.glob("*.schema.json"):
        schema = _load_schema(schema_path, schema_path.name)
        resource = Resource.from_contents(schema, default_specification=DRAFT202012)
        resources.append((schema_path.resolve().as_uri(), resource))
        resources.append((schema_path.name, resource))
        schema_id = schema.get("$id")
        if isinstance(schema_id, str):
            resources.append((schema_id, resource))
            base = schema_id.rsplit("/", 1)[0]
            resources.append((f"{base}/{schema_path.name}", resource))
    return Registry().with_resources(resources)


def _schemas_dir() -> Path:
    contracts_root = os.environ.get("TRADECRAFT_CONTRACTS_ROOT")
    if contracts_root:
        return Path(contracts_root) / SCHEMAS_RELATIVE_PATH

    for ancestor in Path(__file__).resolve().parents:
        candidate = ancestor / "tradecraft-contracts" / SCHEMAS_RELATIVE_PATH
        if candidate.is_dir():
            return candidate

    return Path(__file__).parent / "schemas"


def _agent_runtime_schemas_dir() -> Path:
    contracts_root = os.environ.get("TRADECRAFT_CONTRACTS_ROOT")
    if contracts_root:
        return Path(contracts_root) / AGENT_RUNTIME_SCHEMAS_RELATIVE_PATH

    for ancestor in Path(__file__).resolve().parents:
        candidate = ancestor / "tradecraft-contracts" / AGENT_RUNTIME_SCHEMAS_RELATIVE_PATH
        if candidate.is_dir():
            return candidate

    return Path(__file__).parent / "agent-runtime-schemas"
