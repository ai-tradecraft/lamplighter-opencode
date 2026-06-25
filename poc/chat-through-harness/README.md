# Chat Through Harness POC

ASP.NET + React proof of concept for sending a prompt through the
`lamplighter-opencode` harness.

## Run

From this folder:

```sh
dotnet run --project src/ChatThroughHarness.Api
```

In another terminal:

```sh
cd client
npm install
npm run dev
```

The API defaults to the deterministic fake harness. To use the Python CLI
adapter:

```sh
CHAT_HARNESS_MODE=cli dotnet run --project src/ChatThroughHarness.Api
```

Runtime files are written under `.agent-runtime/`.

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
