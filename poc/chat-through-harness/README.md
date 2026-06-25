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
