#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
POC="$ROOT/poc/chat-through-harness"
CLIENT="$POC/client"

cd "$ROOT"

echo "==> Python harness checks"
uv run ruff format --check .
uv run ruff check .
uv run ty check --output-format=concise .
uv run pytest

echo "==> ASP.NET POC build and tests"
dotnet build "$POC/ChatThroughHarness.sln"
dotnet test "$POC/ChatThroughHarness.sln" --no-build

echo "==> React client build"
cd "$CLIENT"
npm ci
npm run build

if [[ "${RUN_REAL_BACKEND:-0}" == "1" ]]; then
  echo "==> Real OpenCode backend integration test"
  cd "$ROOT"
  make test-real-backend
else
  echo "==> Skipping real backend integration test"
  echo "    Set RUN_REAL_BACKEND=1 to include make test-real-backend."
fi

echo "==> Chat Through Harness POC checks completed"
