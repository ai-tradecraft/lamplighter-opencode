#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
POC="$ROOT/poc/chat-through-harness"
CLIENT="$POC/client"
SCRIPT="$ROOT/scripts/run-chat-through-harness.sh"
API_URL="${CHAT_THROUGH_HARNESS_API_URL:-http://127.0.0.1:5087}"
PORTAL_URL="${CHAT_THROUGH_HARNESS_URL:-http://127.0.0.1:5173}"
WINDOW_NAME="${CHAT_THROUGH_HARNESS_TMUX_WINDOW:-chat-poc}"
API_CONTRACT_VERSION=2
LAUNCHER_LOCK_DIR=""

controller_workspace() {
  if [[ -n "${LAMPLIGHTER_CONTROLLER_WORKSPACE:-}" ]]; then
    printf '%s\n' "$LAMPLIGHTER_CONTROLLER_WORKSPACE"
    return
  fi

  case "$(uname -s)" in
    Darwin)
      printf '%s\n' "$HOME/Library/Application Support/lamplighter/workspace"
      ;;
    MINGW*|MSYS*|CYGWIN*)
      printf '%s\n' "${LOCALAPPDATA:?LOCALAPPDATA is required}/lamplighter/workspace"
      ;;
    *)
      printf '%s\n' "${XDG_DATA_HOME:-$HOME/.local/share}/lamplighter/workspace"
      ;;
  esac
}

central_log_root() {
  printf '%s\n' "${CHAT_THROUGH_HARNESS_LOG_ROOT:-$(controller_workspace)/logs}"
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

api_contract_is_ready() {
  curl --fail --silent "${1%/}/api/system/info" \
    | grep -q "\"contractVersion\":$API_CONTRACT_VERSION"
}

wait_for_api_contract() {
  local base_url="$1"
  local attempts="${CHAT_THROUGH_HARNESS_STARTUP_ATTEMPTS:-120}"
  local attempt

  printf 'Waiting for compatible Tradecraft API at %s ...\n' "$base_url"
  for ((attempt = 1; attempt <= attempts; attempt++)); do
    if api_contract_is_ready "$base_url"; then
      printf 'Tradecraft API contract version %s is ready.\n' "$API_CONTRACT_VERSION"
      return 0
    fi
    sleep 1
  done

  printf 'A compatible Tradecraft API did not become ready after %s seconds: %s\n' \
    "$attempts" "$base_url" >&2
  return 1
}

owned_service_pids() {
  ps -axo pid=,command= | awk -v root="$POC" '
    index($0, root "/src/ChatThroughHarness.Api/bin/") ||
    index($0, root "/src/ChatThroughHarness.Runner/bin/") ||
    index($0, root "/client/node_modules/.bin/vite") {
      print $1
    }
  '
}

stop_owned_services() {
  local pids remaining attempt
  pids="$(owned_service_pids | xargs 2>/dev/null || true)"
  if [[ -z "$pids" ]]; then
    return
  fi

  printf 'Stopping previous chat POC service processes: %s\n' "$pids"
  # shellcheck disable=SC2086
  kill $pids 2>/dev/null || true
  for ((attempt = 1; attempt <= 20; attempt++)); do
    remaining="$(owned_service_pids | xargs 2>/dev/null || true)"
    if [[ -z "$remaining" ]]; then
      return
    fi
    sleep 0.25
  done

  printf 'Forcing previous chat POC service processes to stop: %s\n' "$remaining"
  # shellcheck disable=SC2086
  kill -KILL $remaining 2>/dev/null || true
}

remove_previous_tmux_windows() {
  local current_window window_id window_name
  if [[ -z "${TMUX:-}" ]]; then
    return
  fi
  current_window="$(tmux display-message -p -t "${TMUX_PANE:?}" '#{window_id}')"
  while IFS=$'\t' read -r window_id window_name; do
    if [[ "$window_id" != "$current_window" && "$window_name" == "$WINDOW_NAME" ]]; then
      tmux kill-window -t "$window_id" 2>/dev/null || true
    fi
  done < <(tmux list-windows -a -F '#{window_id}	#{window_name}')
}

prepare_clean_start() {
  local lock_root lock_dir
  lock_root="$(controller_workspace)"
  lock_dir="$lock_root/.launcher-lock"
  mkdir -p "$lock_root"
  if ! mkdir "$lock_dir" 2>/dev/null; then
    printf 'Another chat POC launch is already in progress: %s\n' "$lock_dir" >&2
    exit 1
  fi
  LAUNCHER_LOCK_DIR="$lock_dir"
  trap cleanup_launcher_lock EXIT

  remove_previous_tmux_windows
  stop_owned_services
}

cleanup_launcher_lock() {
  if [[ -n "$LAUNCHER_LOCK_DIR" ]]; then
    rmdir "$LAUNCHER_LOCK_DIR" 2>/dev/null || true
  fi
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
  export LAMPLIGHTER_CONTROLLER_WORKSPACE="${LAMPLIGHTER_CONTROLLER_WORKSPACE:-$(controller_workspace)}"
  export LAMPLIGHTER_OPENCODE_CONFIG_MODE="${LAMPLIGHTER_OPENCODE_CONFIG_MODE:-inherit-global}"
  wait_for_api_contract "${Runner__OrchestratorBaseUri%/}"
  cd "$POC"
  run_logged runner dotnet run --project src/ChatThroughHarness.Runner
}

run_ui() {
  wait_for_api_contract "${API_URL%/}"
  cd "$CLIENT"
  if [[ ! -d node_modules ]]; then
    npm install
  fi
  run_logged ui npm run dev -- --strictPort
}

open_portal() {
  wait_for_api_contract "${API_URL%/}"
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

  prepare_clean_start

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
  stop)
    remove_previous_tmux_windows
    stop_owned_services
    printf 'Stopped chat POC API, runner, and UI processes.\n'
    ;;
  *)
    printf 'Usage: %s [start|stop]\n' "$0" >&2
    exit 1
    ;;
esac
