#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
POC="$ROOT/poc/chat-through-harness"
CLIENT="$POC/client"
SCRIPT="$ROOT/scripts/run-chat-through-harness.sh"
API_URL="${CHAT_THROUGH_HARNESS_API_URL:-http://127.0.0.1:5087}"
PORTAL_URL="${CHAT_THROUGH_HARNESS_URL:-http://127.0.0.1:5173}"
WINDOW_NAME="${CHAT_THROUGH_HARNESS_TMUX_WINDOW:-chat-poc}"

central_log_root() {
  printf '%s\n' "${CHAT_THROUGH_HARNESS_LOG_ROOT:-$POC/.agent-runtime/logs}"
}

run_logged() {
  local component="$1"
  shift
  local log_root log_file exit_code
  log_root="$(central_log_root)"
  log_file="$log_root/$component.log"
  mkdir -p "$log_root"
  export CHAT_THROUGH_HARNESS_LOG_ROOT="$log_root"

  printf '\n[%s] Starting %s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" "$component" \
    | tee -a "$log_file"
  set +e
  "$@" 2>&1 | tee -a "$log_file"
  exit_code="${PIPESTATUS[0]}"
  set -e
  printf '[%s] %s exited with status %s\n' \
    "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" "$component" "$exit_code" \
    | tee -a "$log_file"
  return "$exit_code"
}

source_env() {
  local env_file="$1"
  if [[ -f "$env_file" ]]; then
    set -a
    # shellcheck disable=SC1090
    source "$env_file"
    set +a
  fi
}

wait_for_service() {
  local service_name="$1"
  local url="$2"
  local attempts="${CHAT_THROUGH_HARNESS_STARTUP_ATTEMPTS:-120}"
  local attempt

  printf 'Waiting for %s at %s ...\n' "$service_name" "$url"
  for ((attempt = 1; attempt <= attempts; attempt++)); do
    if curl --fail --silent "$url" >/dev/null 2>&1; then
      printf '%s is ready.\n' "$service_name"
      return 0
    fi
    sleep 1
  done

  printf '%s did not become ready after %s seconds: %s\n' \
    "$service_name" "$attempts" "$url" >&2
  return 1
}

run_api() {
  source_env "$POC/src/ChatThroughHarness.Api/.env"
  export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://127.0.0.1:5087}"
  cd "$POC"
  run_logged api dotnet run --project src/ChatThroughHarness.Api
}

run_runner() {
  source_env "$ROOT/.env"
  source_env "$POC/src/ChatThroughHarness.Runner/.env"
  export Runner__RunnerId="${Runner__RunnerId:-runner_local}"
  export Runner__OrchestratorBaseUri="${Runner__OrchestratorBaseUri:-http://127.0.0.1:5087}"
  export LAMPLIGHTER_OPENCODE_CONFIG_MODE="${LAMPLIGHTER_OPENCODE_CONFIG_MODE:-inherit-global}"
  wait_for_service \
    "Tradecraft API" \
    "${Runner__OrchestratorBaseUri%/}/api/agent-controllers"
  cd "$POC"
  run_logged runner dotnet run --project src/ChatThroughHarness.Runner
}

run_ui() {
  wait_for_service "Tradecraft API" "${API_URL%/}/api/agent-controllers"
  cd "$CLIENT"
  if [[ ! -d node_modules ]]; then
    npm install
  fi
  run_logged ui npm run dev -- --strictPort
}

open_portal() {
  wait_for_service "Chat portal" "$PORTAL_URL"
  if command -v open >/dev/null 2>&1; then
    open "$PORTAL_URL"
  elif command -v xdg-open >/dev/null 2>&1; then
    xdg-open "$PORTAL_URL"
  else
    printf 'Portal ready: %s\n' "$PORTAL_URL"
  fi
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || {
    printf 'Required command not found: %s\n' "$1" >&2
    exit 1
  }
}

start_tmux_window() {
  if [[ -z "${TMUX:-}" ]]; then
    printf 'Run this command from inside a tmux session.\n' >&2
    exit 1
  fi

  require_command tmux
  require_command dotnet
  require_command npm
  require_command curl

  local api_command runner_command ui_command browser_command
  local window_id api_pane runner_pane ui_pane
  printf -v api_command '%q pane api' "$SCRIPT"
  printf -v runner_command '%q pane runner' "$SCRIPT"
  printf -v ui_command '%q pane ui' "$SCRIPT"
  printf -v browser_command '%q open-browser' "$SCRIPT"

  read -r window_id api_pane < <(
    tmux new-window -d -P -F '#{window_id} #{pane_id}' \
      -n "$WINDOW_NAME" -c "$POC" "$api_command"
  )
  tmux set-option -w -t "$window_id" remain-on-exit on
  tmux select-pane -t "$api_pane" -T api

  runner_pane="$(
    tmux split-window -d -h -P -F '#{pane_id}' \
      -t "$api_pane" -c "$POC" "$runner_command"
  )"
  tmux select-pane -t "$runner_pane" -T runner

  ui_pane="$(
    tmux split-window -d -v -P -F '#{pane_id}' \
      -t "$api_pane" -c "$CLIENT" "$ui_command"
  )"
  tmux select-pane -t "$ui_pane" -T ui
  tmux select-layout -t "$window_id" tiled >/dev/null

  tmux run-shell -b "$browser_command"
  tmux select-window -t "$window_id"
  tmux select-pane -t "$api_pane"

  printf 'Started API, runner, and UI in tmux window %s.\n' "$WINDOW_NAME"
  printf 'The browser will open when %s is ready.\n' "$PORTAL_URL"
}

case "${1:-start}" in
  start)
    start_tmux_window
    ;;
  pane)
    case "${2:-}" in
      api) run_api ;;
      runner) run_runner ;;
      ui) run_ui ;;
      *)
        printf 'Unknown pane service: %s\n' "${2:-}" >&2
        exit 1
        ;;
    esac
    ;;
  open-browser)
    open_portal
    ;;
  *)
    printf 'Usage: %s [start]\n' "$0" >&2
    exit 1
    ;;
esac
