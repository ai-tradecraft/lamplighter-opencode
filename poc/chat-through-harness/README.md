# Chat Through Harness POC

ASP.NET + React proof of concept for sending a prompt through the
`lamplighter-opencode` harness.

## Run

Optional local env examples are available in:

- `../../.env.example` for Lamplighter/OpenCode backend variables.
- `src/ChatThroughHarness.Api/.env.example` for API variables.
- `src/ChatThroughHarness.Runner/.env.example` for runner variables.
- `client/.env.example` for Vite/React variables.

When already inside a tmux session, start all three services and open the
portal with:

```sh
make run-chat-through-harness
```

This creates a `chat-poc` tmux window with separate API, runner, and UI panes.
Before launching, the script stops earlier API, runner, and UI processes from
this checkout. It then waits for the API contract expected by the portal before
starting the runner or UI. This prevents a newly built portal from silently
connecting to an older API that still owns port `5087`.

Stop the three POC services explicitly with:

```bash
make stop-chat-through-harness
```
It opens `http://127.0.0.1:5173` in the default browser as soon as Vite is
ready. The runner and UI wait for the API readiness endpoint before starting,
so their initial requests do not race the ASP.NET startup. The runner loads
backend variables from the repo-root `.env`, then applies any overrides from
`poc/chat-through-harness/src/ChatThroughHarness.Runner/.env`.

To use a different tmux window name or portal URL:

```sh
CHAT_THROUGH_HARNESS_TMUX_WINDOW=my-chat \
CHAT_THROUGH_HARNESS_URL=http://127.0.0.1:5173 \
make run-chat-through-harness
```

For manual startup, use the following three-terminal workflow.

From the `lamplighter-opencode` repo root, start the API:

```sh
cd poc/chat-through-harness
dotnet run --project src/ChatThroughHarness.Api
```

In another terminal, start the runner. The runner is the local process that
polls the API for work, prepares Lamplighter sessions, starts `opencode serve`,
submits turns, and reports results/events back.

```sh
cd poc/chat-through-harness
dotnet run --project src/ChatThroughHarness.Runner
```

In a third terminal, start the React UI:

```sh
cd poc/chat-through-harness/client
npm install
npm run dev
```

Open the Vite URL shown in the client terminal, usually
`http://127.0.0.1:5173`.

The portal opens on the active controller list. Select a controller to inspect
its heartbeat and allocated agents, select an agent to inspect its runtime and
available sessions, and select a session to open its chat, runtime events, and
diagnostics. The local runner reports its controller heartbeat and local agent
inventory every few seconds.

Each provisioned agent owns an isolated workspace, runtime directory, and
OpenCode server. An agent can own multiple chat sessions. Sessions share their
agent's workspace and server but retain independent conversation state, events,
artifacts, and cancellation lifecycle.

Agent and session lists hide terminal resources by default. Select **Show all**
to include historical agents or sessions.

## Chat History

OpenCode is the authoritative source for user and assistant message content.
When the portal opens a session, it requests
`POST /api/agent-sessions/{sessionId}/history/sync`. The API queues an outbound
runner command; the local runner reads the OpenCode session, uploads a
normalized snapshot by claim check, and emits
`agent_session.history_synced`. The portal refreshes its transcript when that
event arrives.

Tradecraft retains responsibility for command status, failures, diagnostics,
ownership, permissions, and audit metadata. Its transcript is therefore an
enriched projection of OpenCode messages rather than an independent
conversation. Stable OpenCode message IDs make repeated synchronization
idempotent and allow history created outside the portal to appear there.

If the local OpenCode service is unavailable, the runner emits
`agent_session.history_sync_failed`. The session remains usable when otherwise
ready, and the portal keeps the last successfully projected transcript. No
cloud component connects directly to the local OpenCode endpoint.

Controllers disappear from the active list after 30 seconds without a fresh
heartbeat. The API also rejects new-agent requests targeting a missing or stale
controller so commands cannot remain queued for a runner that no longer polls.
API tests use an isolated temporary runtime root and never register test
controllers in the development `.agent-runtime`.

The browser-facing API enqueues commands for the local runner and does not
construct local filesystem paths. The runner owns its local storage beneath
`LAMPLIGHTER_CONTROLLER_WORKSPACE`.

When that variable is not set, the controller workspace defaults to:

```text
macOS:   $HOME/Library/Application Support/lamplighter/workspace
Linux:  ${XDG_DATA_HOME:-$HOME/.local/share}/lamplighter/workspace
Windows: %LOCALAPPDATA%\lamplighter\workspace
```

The resulting layout is:

```text
<controller-workspace>/agents/<agent-id>/
|-- workspace/
`-- runtime/
    |-- opencode-server.json
    |-- logs/
    `-- sessions/<session-id>/
```

## Test

```sh
make test-chat-through-harness
```

That target runs:

- Python harness checks: Ruff format, Ruff lint, ty, and pytest.
- ASP.NET checks: solution build and tests.
- React checks: `npm ci` and production build.

To include the opt-in real OpenCode backend integration test:

```sh
RUN_REAL_BACKEND=1 make test-chat-through-harness
```

The API keeps its POC control-plane records under
`poc/chat-through-harness/.agent-runtime/`. Agent workspaces and runtime files
are stored under the controller workspace described above.

## Central Logs

`make run-chat-through-harness` writes component logs under
`<controller-workspace>/logs/`:

- `api.log`: ASP.NET lifecycle, requests, and failures.
- `runner.log`: controller heartbeats, command processing, and harness process
  lifecycle.
- `ui.log`: Vite development-server output.
- `browser.jsonl`: sanitized browser, API-request, SignalR, and UI lifecycle
  diagnostics posted by the React client.
- `agents/<agent-id>/runtime/logs/opencode/`: per-agent
  `opencode serve` output.

Application-generated lifecycle entries do not intentionally include prompt
bodies, model responses, credentials, or environment-variable values.
OpenCode and third-party plugin output can contain additional backend details,
so treat the log directory as sensitive local diagnostic data. Override the
directory with `CHAT_THROUGH_HARNESS_LOG_ROOT`.

Watch all top-level component logs:

```sh
tail -F "$LAMPLIGHTER_CONTROLLER_WORKSPACE"/logs/{api,runner,ui}.log \
  "$LAMPLIGHTER_CONTROLLER_WORKSPACE"/logs/browser.jsonl
```

## Real Backend Configuration

The real backend test uses OpenCode through the Lamplighter harness:

```sh
make test-real-backend
```

For local development only, this target sources a gitignored `.env` file from
the `lamplighter-opencode` repo root and sets
`LAMPLIGHTER_OPENCODE_USE_REAL_BACKEND=1` and
`LAMPLIGHTER_OPENCODE_CONFIG_MODE=project-only`.

Expected provider variables:

```text
AZURE_OPENAI_API_KEY
AZURE_OPENAI_ENDPOINT
AZURE_OPENAI_DEPLOYMENT
```

The `.env` file is only a developer convenience. In production, these values
should come from the runner's injected process environment, populated by the
deployment platform's secret system, such as Kubernetes Secrets, Azure Key
Vault references, CI secret variables, Aspire configuration, Docker Compose, or
another approved provider.

Lamplighter should persist only required environment variable names or secret
references, never secret values. The runner resolves the values at launch time
and injects them into the OpenCode process.

## OpenCode Configuration Modes

The POC supports explicit OpenCode configuration modes through
`LAMPLIGHTER_OPENCODE_CONFIG_MODE`.

- `inherit-global` is the default for local developer use. OpenCode may read
  the user's global configuration so personal credentials, MCPs, skills, hooks,
  agents, policies, and model defaults continue to work.
- `project-only` is used by `make test-real-backend` and deterministic backend
  checks. The harness writes a session-local `workspace/opencode.json`, sets
  session-local `HOME`, `XDG_CONFIG_HOME`, `XDG_DATA_HOME`,
  `XDG_CACHE_HOME`, `OPENCODE_CONFIG`, and `OPENCODE_CONFIG_DIR`, and runs
  `opencode run --pure --dir <workspace> --model azure/<deployment>`.
- `managed` is reserved for production-style execution where the runner or
  platform injects approved configuration and secrets.

Use `project-only` or `managed` when a test or work session must prove it used
the intended backend, credentials, model, and policy. Use `inherit-global` when
the goal is a natural local workflow that benefits from the developer's existing
OpenCode setup.
