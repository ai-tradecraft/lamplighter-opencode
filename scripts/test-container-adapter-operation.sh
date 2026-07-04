#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONTAINER_RUNTIME="${CONTAINER_RUNTIME:-docker}"
IMAGE="${LAMPLIGHTER_OPENCODE_CONTAINER_IMAGE:-lamplighter-opencode:local}"
CONTRACTS_ROOT="${TRADECRAFT_CONTRACTS_ROOT:-$(cd "$ROOT/../tradecraft-contracts" && pwd)}"

if ! command -v "$CONTAINER_RUNTIME" >/dev/null 2>&1; then
  echo "Container runtime not found: $CONTAINER_RUNTIME" >&2
  exit 1
fi

if [ ! -d "$CONTRACTS_ROOT/contracts/agent-runtime/v1/schemas" ]; then
  echo "TRADECRAFT_CONTRACTS_ROOT does not contain agent-runtime schemas: $CONTRACTS_ROOT" >&2
  exit 1
fi

workspace="$(mktemp -d "${TMPDIR:-/tmp}/lamplighter-container-proof.XXXXXX")"
operation_dir="$workspace/commands/cmd_describe_container"
operation_path="$operation_dir/adapter-operation.json"
mkdir -p "$operation_dir"

python3 - "$operation_path" "$workspace" <<'PY'
import json
import sys

operation_path = sys.argv[1]
workspace = sys.argv[2]
operation = {
    "message_type": "adapter.operation",
    "protocol_version": "1.0",
    "schema_version": "1.0",
    "operation_id": "cmd_describe_container",
    "operation_type": "DescribeAdapter",
    "idempotency_key": "cmd_describe_container",
    "target": {"controller_id": "controller_container_proof"},
    "deadline": "2026-07-02T00:00:00Z",
    "fencing_token": 1,
    "correlation": {"command_id": "cmd_describe_container"},
    "authorization_context": {
        "subject_ref": "system://container-proof",
        "grant_ref": "authorization-grant://container-proof/1",
        "issued_at": "2026-07-02T00:00:00Z",
    },
    "extensions": {
        "tradecraft.dev/controller_workspace": workspace,
    },
}
with open(operation_path, "w", encoding="utf-8") as handle:
    json.dump(operation, handle)
PY

"$CONTAINER_RUNTIME" build -t "$IMAGE" -f "$ROOT/Containerfile" "$ROOT"

output="$(
  "$CONTAINER_RUNTIME" run --rm \
    -e "TRADECRAFT_CONTRACTS_ROOT=$CONTRACTS_ROOT" \
    -e "LAMPLIGHTER_ADAPTER_DEPLOYMENT_MODE=local_container" \
    -v "$workspace:$workspace" \
    -v "$CONTRACTS_ROOT:$CONTRACTS_ROOT:ro" \
    "$IMAGE" adapter-operation --operation "$operation_path" --json
)"

uv run python - "$output" "$CONTRACTS_ROOT" "$ROOT" <<'PY'
import json
import os
import sys
from pathlib import Path
from urllib.parse import unquote, urlparse

sys.path.insert(0, str(Path(sys.argv[3]) / "src"))
os.environ["TRADECRAFT_CONTRACTS_ROOT"] = sys.argv[2]

from lamplighter_opencode.contracts.validation import validate_agent_runtime_contract

envelope = json.loads(sys.argv[1])
validate_agent_runtime_contract("runtime-adapter-message.schema.json", envelope)
result_ref = envelope["result_ref"]
descriptor_path = Path(unquote(urlparse(result_ref["uri"]).path))
descriptor = json.loads(descriptor_path.read_text(encoding="utf-8"))
validate_agent_runtime_contract("runtime-adapter-message.schema.json", descriptor)
execution_environment = descriptor["composition"]["execution_environment"]
if execution_environment["kind"] != "local_container":
    raise SystemExit(f"Expected local_container, got {execution_environment['kind']}")
if "local_container" not in descriptor["deployment_modes"]:
    raise SystemExit("Descriptor does not advertise local_container deployment support.")
print("Container adapter operation proof passed: DescribeAdapter returned local_container.")
PY
