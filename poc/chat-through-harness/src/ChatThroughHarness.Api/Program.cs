using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatThroughHarness.Protocol;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls("http://127.0.0.1:5087");
}

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
            .SetIsOriginAllowed(_ => true);
    });
});
builder.Services.AddSignalR();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AgentSessionStore>();
builder.Services.AddSingleton<RunnerControlStore>();
builder.Services.AddSingleton<HarnessClientFactory>();
builder.Services.AddSingleton<IHarnessClient>(sp => sp.GetRequiredService<HarnessClientFactory>().Create());

var app = builder.Build();

app.UseCors();
app.MapHub<AgentSessionHub>("/hubs/agent-sessions");

app.MapGet("/api/runner/commands", async (
    string runnerId,
    string? wait,
    RunnerControlStore runnerStore,
    CancellationToken cancellationToken) =>
{
    var deadline = DateTimeOffset.UtcNow + ParseLongPollWait(wait);
    while (!cancellationToken.IsCancellationRequested)
    {
        var commands = await runnerStore.GetAvailableCommandsAsync(runnerId, cancellationToken);
        if (commands.Count > 0 || DateTimeOffset.UtcNow >= deadline)
        {
            return Results.Ok(commands);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
    }

    return Results.Ok(Array.Empty<RunnerCommandEnvelope>());
});

app.MapPost("/api/runner/commands/{commandId}/claim", async (
    string commandId,
    ClaimRunnerCommandRequest request,
    RunnerControlStore runnerStore,
    CancellationToken cancellationToken) =>
{
    var result = await runnerStore.ClaimCommandAsync(commandId, request, cancellationToken);
    return result.Status switch
    {
        RunnerCommandMutationStatus.NotFound => Results.NotFound(),
        RunnerCommandMutationStatus.Conflict => Results.Conflict(new { message = result.Message }),
        _ => Results.Ok(result.Command)
    };
});

app.MapPost("/api/runner/commands/{commandId}/complete", async (
    string commandId,
    CompleteRunnerCommandRequest request,
    RunnerControlStore runnerStore,
    CancellationToken cancellationToken) =>
{
    var result = await runnerStore.CompleteCommandAsync(commandId, request, cancellationToken);
    return result.Status switch
    {
        RunnerCommandMutationStatus.NotFound => Results.NotFound(),
        RunnerCommandMutationStatus.Conflict => Results.Conflict(new { message = result.Message }),
        _ => Results.Ok(result.Command)
    };
});

app.MapPost("/api/runner/events", async (
    RunnerEventEnvelope runnerEvent,
    RunnerControlStore runnerStore,
    AgentSessionStore sessionStore,
    IHubContext<AgentSessionHub> hub,
    CancellationToken cancellationToken) =>
{
    await runnerStore.AddEventAsync(runnerEvent, cancellationToken);
    await ApplyRunnerEventAsync(runnerEvent, runnerStore, sessionStore, cancellationToken);
    await PublishAsync(
        sessionStore,
        hub,
        runnerEvent.AgentSessionId,
        RunnerEventTurnId(runnerEvent),
        runnerEvent.Type,
        runnerEvent,
        cancellationToken);
    return Results.Accepted();
});

app.MapPost("/api/runner/heartbeat", async (
    RunnerHeartbeat heartbeat,
    RunnerControlStore runnerStore,
    AgentSessionStore sessionStore,
    CancellationToken cancellationToken) =>
{
    await runnerStore.UpsertRunnerHeartbeatAsync(heartbeat, cancellationToken);
    foreach (var agent in heartbeat.Agents)
    {
        await sessionStore.UpsertInventorySessionAsync(heartbeat.RunnerId, agent, cancellationToken);
    }

    return Results.Accepted();
});

app.MapPost("/api/runner/content", async (
    ClaimCheckContentUploadRequest request,
    RunnerControlStore runnerStore,
    CancellationToken cancellationToken) =>
{
    try
    {
        var response = await runnerStore.SaveContentAsync(request, cancellationToken);
        return Results.Created(response.ContentRef.Uri, response);
    }
    catch (InvalidDataException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapGet("/api/runner/content/{contentId}", async (
    string contentId,
    RunnerControlStore runnerStore,
    CancellationToken cancellationToken) =>
{
    var content = await runnerStore.GetContentAsync(contentId, cancellationToken);
    return content is null
        ? Results.NotFound()
        : Results.File(content.Value.Bytes, content.Value.ContentType);
});

app.MapGet("/api/agent-controllers", async (
    RunnerControlStore runnerStore,
    CancellationToken cancellationToken) =>
{
    var heartbeats = await runnerStore.GetRunnerHeartbeatsAsync(cancellationToken);
    return Results.Ok(heartbeats.Select(AgentControllerRecord.FromHeartbeat).ToArray());
});

app.MapGet("/api/agent-controllers/{runnerId}", async (
    string runnerId,
    RunnerControlStore runnerStore,
    CancellationToken cancellationToken) =>
{
    var heartbeat = await runnerStore.GetRunnerHeartbeatAsync(runnerId, cancellationToken);
    return heartbeat is null ? Results.NotFound() : Results.Ok(AgentControllerRecord.FromHeartbeat(heartbeat));
});

app.MapPost("/api/agent-sessions", async (
    CreateAgentSessionRequest request,
    AgentSessionStore store,
    RunnerControlStore runnerStore,
    IHubContext<AgentSessionHub> hub,
    CancellationToken cancellationToken) =>
{
    var session = AgentSessionRecord.Create(request, RuntimePaths.Root);
    await store.UpsertSessionAsync(session, cancellationToken);
    await PublishAsync(store, hub, session.Id, null, "agent_session.preparing", new { session.Id }, cancellationToken);

    var spec = AgentSessionSpec.FromSession(session);
    var payloadRef = await runnerStore.SaveJsonContentAsync(spec, "application/vnd.tradecraft.agent-session-spec+json", cancellationToken);
    var command = RunnerCommandFactory.Create(
        session.Id,
        RunnerCommandTypes.PrepareAgentSession,
        payloadRef,
        correlationId: session.Id,
        idempotencyKey: session.Id,
        runnerId: request.ControllerId);
    await runnerStore.EnqueueCommandAsync(command, cancellationToken);
    await PublishAsync(store, hub, session.Id, null, "agent_session.command_queued", command, cancellationToken);

    return Results.Created($"/api/agent-sessions/{session.Id}", session);
});

app.MapGet("/api/agent-sessions/{sessionId}", async (string sessionId, AgentSessionStore store, CancellationToken cancellationToken) =>
{
    var session = await store.GetSessionAsync(sessionId, cancellationToken);
    return session is null ? Results.NotFound() : Results.Ok(session);
});

app.MapPost("/api/agent-sessions/{sessionId}/turns", async (
    string sessionId,
    SubmitTurnRequest request,
    AgentSessionStore store,
    RunnerControlStore runnerStore,
    IHubContext<AgentSessionHub> hub,
    CancellationToken cancellationToken) =>
{
    var session = await store.GetSessionAsync(sessionId, cancellationToken);
    if (session is null)
    {
        return Results.NotFound();
    }

    if (session.Status is "cancelled" or "cancelling" or "failed")
    {
        return Results.BadRequest(new { message = $"Session is {session.Status}." });
    }

    var turn = AgentTurnRecord.Create(sessionId, request.Prompt);
    await store.UpsertTurnAsync(turn, cancellationToken);
    await PublishAsync(store, hub, sessionId, turn.Id, "agent_turn.submitted", turn, cancellationToken);

    var turnRequest = AgentTurnRequest.FromTurn(turn);
    var payloadRef = await runnerStore.SaveJsonContentAsync(turnRequest, "application/vnd.tradecraft.agent-turn-request+json", cancellationToken);
    var command = RunnerCommandFactory.Create(
        sessionId,
        RunnerCommandTypes.SubmitAgentTurn,
        payloadRef,
        correlationId: turn.Id,
        idempotencyKey: turn.Id);
    await runnerStore.EnqueueCommandAsync(command, cancellationToken);
    await PublishAsync(store, hub, sessionId, turn.Id, "agent_turn.command_queued", command, cancellationToken);

    return Results.Created($"/api/agent-sessions/{sessionId}/turns/{turn.Id}", turn);
});

app.MapGet("/api/agent-sessions/{sessionId}/turns/{turnId}", async (
    string sessionId,
    string turnId,
    AgentSessionStore store,
    CancellationToken cancellationToken) =>
{
    var turn = await store.GetTurnAsync(sessionId, turnId, cancellationToken);
    return turn is null ? Results.NotFound() : Results.Ok(turn);
});

app.MapGet("/api/agent-sessions/{sessionId}/turns", async (
    string sessionId,
    AgentSessionStore store,
    CancellationToken cancellationToken) =>
{
    var turns = await store.GetTurnsAsync(sessionId, cancellationToken);
    return Results.Ok(turns);
});

app.MapGet("/api/agent-sessions/{sessionId}/events", async (
    string sessionId,
    AgentSessionStore store,
    CancellationToken cancellationToken) =>
{
    var events = await store.GetEventsAsync(sessionId, cancellationToken);
    return Results.Ok(events);
});

app.MapPost("/api/agent-sessions/{sessionId}/cancel", async (
    string sessionId,
    CancelSessionRequest request,
    AgentSessionStore store,
    RunnerControlStore runnerStore,
    IHubContext<AgentSessionHub> hub,
    CancellationToken cancellationToken) =>
{
    var session = await store.GetSessionAsync(sessionId, cancellationToken);
    if (session is null)
    {
        return Results.NotFound();
    }

    var payload = new { reason = request.Reason ?? "Cancelled by operator." };
    var payloadRef = await runnerStore.SaveJsonContentAsync(payload, "application/vnd.tradecraft.agent-session-cancel+json", cancellationToken);
    var command = RunnerCommandFactory.Create(
        sessionId,
        RunnerCommandTypes.CancelAgentSession,
        payloadRef,
        correlationId: sessionId,
        idempotencyKey: $"cancel:{sessionId}");
    await runnerStore.EnqueueCommandAsync(command, cancellationToken);
    session = session with { Status = "cancelling", EndedAt = DateTimeOffset.UtcNow };
    await store.UpsertSessionAsync(session, cancellationToken);
    await PublishAsync(store, hub, sessionId, null, "agent_session.cancel_command_queued", command, cancellationToken);
    return Results.Ok(session);
});

app.Run();

static TimeSpan ParseLongPollWait(string? wait)
{
    if (string.IsNullOrWhiteSpace(wait))
    {
        return TimeSpan.FromSeconds(30);
    }

    if (wait.EndsWith('s') && int.TryParse(wait[..^1], out var seconds))
    {
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 120));
    }

    return int.TryParse(wait, out seconds)
        ? TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 120))
        : TimeSpan.FromSeconds(30);
}

static async Task ApplyRunnerEventAsync(
    RunnerEventEnvelope runnerEvent,
    RunnerControlStore runnerStore,
    AgentSessionStore sessionStore,
    CancellationToken cancellationToken)
{
    switch (runnerEvent.Type)
    {
        case RunnerEventTypes.AgentSessionReady:
        {
            var session = await sessionStore.GetSessionAsync(runnerEvent.AgentSessionId, cancellationToken);
            if (session is not null)
            {
                await sessionStore.UpsertSessionAsync(
                    session with
                    {
                        Status = "ready",
                        ReadyAt = runnerEvent.CreatedAt,
                        FailedAt = null,
                        FailureSummary = null
                    },
                    cancellationToken);
            }
            break;
        }

        case RunnerEventTypes.AgentSessionFailed:
        {
            var session = await sessionStore.GetSessionAsync(runnerEvent.AgentSessionId, cancellationToken);
            if (session is not null)
            {
                await sessionStore.UpsertSessionAsync(
                    session with
                    {
                        Status = "failed",
                        FailedAt = runnerEvent.CreatedAt,
                        FailureSummary = await PayloadSummaryAsync(runnerStore, runnerEvent.PayloadRef, "Agent session failed.", cancellationToken)
                    },
                    cancellationToken);
            }
            break;
        }

        case "agent_session.cancelled":
        {
            var session = await sessionStore.GetSessionAsync(runnerEvent.AgentSessionId, cancellationToken);
            if (session is not null)
            {
                await sessionStore.UpsertSessionAsync(
                    session with { Status = "cancelled", EndedAt = runnerEvent.CreatedAt },
                    cancellationToken);
            }
            break;
        }

        case RunnerEventTypes.AgentTurnCompleted:
        {
            var turn = await sessionStore.GetTurnAsync(runnerEvent.AgentSessionId, runnerEvent.CorrelationId, cancellationToken);
            if (turn is null)
            {
                break;
            }

            var result = await PayloadJsonAsync<AgentTurnResult>(runnerStore, runnerEvent.PayloadRef, cancellationToken);
            await sessionStore.UpsertTurnAsync(
                turn with
                {
                    Status = result?.Status ?? "completed",
                    CompletedAt = runnerEvent.CreatedAt,
                    Response = result?.Message,
                    FailureSummary = result?.FailureReport?.Summary,
                    FailureDetail = result?.FailureReport?.Detail,
                    Diagnostics = result?.Diagnostics
                },
                cancellationToken);
            break;
        }

        case RunnerEventTypes.AgentTurnFailed:
        {
            var turn = await sessionStore.GetTurnAsync(runnerEvent.AgentSessionId, runnerEvent.CorrelationId, cancellationToken);
            if (turn is not null)
            {
                var result = runnerEvent.PayloadRef?.ContentType.StartsWith(
                    "application/vnd.tradecraft.agent-turn-result+json",
                    StringComparison.OrdinalIgnoreCase) == true
                        ? await PayloadJsonAsync<AgentTurnResult>(
                            runnerStore,
                            runnerEvent.PayloadRef,
                            cancellationToken)
                        : null;
                await sessionStore.UpsertTurnAsync(
                    turn with
                    {
                        Status = "failed",
                        CompletedAt = runnerEvent.CreatedAt,
                        Response = result?.Message,
                        FailureSummary = result?.FailureReport?.Summary
                            ?? await PayloadSummaryAsync(
                                runnerStore,
                                runnerEvent.PayloadRef,
                                "Agent turn failed.",
                                cancellationToken),
                        FailureDetail = result?.FailureReport?.Detail,
                        Diagnostics = result?.Diagnostics
                    },
                    cancellationToken);
            }
            break;
        }
    }
}

static async Task<T?> PayloadJsonAsync<T>(
    RunnerControlStore runnerStore,
    ClaimCheckContentRef? payloadRef,
    CancellationToken cancellationToken)
{
    if (payloadRef is null)
    {
        return default;
    }

    var content = await runnerStore.GetContentAsync(ContentId(payloadRef), cancellationToken);
    return content is null
        ? default
        : JsonSerializer.Deserialize<T>(content.Value.Bytes, JsonDefaults.Options);
}

static async Task<string> PayloadSummaryAsync(
    RunnerControlStore runnerStore,
    ClaimCheckContentRef? payloadRef,
    string fallback,
    CancellationToken cancellationToken)
{
    if (payloadRef is null)
    {
        return fallback;
    }

    var content = await runnerStore.GetContentAsync(ContentId(payloadRef), cancellationToken);
    if (content is null)
    {
        return fallback;
    }

    var text = System.Text.Encoding.UTF8.GetString(content.Value.Bytes).Trim();
    return string.IsNullOrWhiteSpace(text)
        ? fallback
        : text.Length <= 500 ? text : text[..500];
}

static string ContentId(ClaimCheckContentRef contentRef)
{
    return contentRef.Uri.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
}

static string? RunnerEventTurnId(RunnerEventEnvelope runnerEvent)
{
    return runnerEvent.Type.StartsWith("agent_turn.", StringComparison.Ordinal)
        ? runnerEvent.CorrelationId
        : null;
}

static async Task PublishAsync(
    AgentSessionStore store,
    IHubContext<AgentSessionHub> hub,
    string sessionId,
    string? turnId,
    string type,
    object payload,
    CancellationToken cancellationToken)
{
    var record = RuntimeEventRecord.Create(sessionId, turnId, type, payload);
    await store.AddEventAsync(record, cancellationToken);
    await hub.Clients.Group(sessionId).SendAsync(type, record, cancellationToken);
}

public partial class Program;

public sealed class AgentSessionHub : Hub
{
    public async Task JoinSession(string sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId);
    }
}

public sealed class HarnessClientFactory(IConfiguration configuration)
{
    public IHarnessClient Create()
    {
        var mode = configuration["CHAT_HARNESS_MODE"] ?? Environment.GetEnvironmentVariable("CHAT_HARNESS_MODE") ?? "fake";
        return string.Equals(mode, "cli", StringComparison.OrdinalIgnoreCase)
            ? new CliHarnessClient()
            : new FakeHarnessClient();
    }
}

public interface IHarnessClient
{
    Task<PrepareSessionResult> PrepareSessionAsync(AgentSessionRecord session, CancellationToken cancellationToken);
    Task<AgentTurnResult> SubmitTurnAsync(AgentSessionRecord session, AgentTurnRecord turn, CancellationToken cancellationToken);
    Task CancelSessionAsync(AgentSessionRecord session, string reason, CancellationToken cancellationToken);
}

public sealed class FakeHarnessClient : IHarnessClient
{
    public async Task<PrepareSessionResult> PrepareSessionAsync(AgentSessionRecord session, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(session.WorkspacePath);
        await File.WriteAllTextAsync(
            Path.Combine(session.RuntimePath, "session.json"),
            JsonSerializer.Serialize(session, JsonDefaults.Options),
            cancellationToken);
        return new PrepareSessionResult(new HarnessDiagnostics("fake", 0, "", "", session.RuntimePath));
    }

    public Task<AgentTurnResult> SubmitTurnAsync(AgentSessionRecord session, AgentTurnRecord turn, CancellationToken cancellationToken)
    {
        var message = $"Fake harness response for `{turn.Prompt}`. Session {session.Id} is ready for CLI/OpenCode wiring.";
        var result = new AgentTurnResult(
            Id: Ids.New("result"),
            AgentSessionId: session.Id,
            RequestId: turn.Id,
            Status: "completed",
            Message: message,
            ArtifactRefs: [],
            ChangedFiles: [],
            CommandsObserved: [],
            FailureReport: null,
            Diagnostics: new HarnessDiagnostics("fake", 0, "", "", session.RuntimePath));
        return Task.FromResult(result);
    }

    public Task CancelSessionAsync(AgentSessionRecord session, string reason, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public sealed class CliHarnessClient : IHarnessClient
{
    public async Task<PrepareSessionResult> PrepareSessionAsync(AgentSessionRecord session, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(session.RuntimePath);
        var specPath = Path.Combine(session.RuntimePath, "agent-session-spec.json");
        var spec = AgentSessionSpec.FromSession(session);
        await File.WriteAllTextAsync(specPath, JsonSerializer.Serialize(spec, JsonDefaults.Options), cancellationToken);

        var command = $"uv run lamplighter-opencode prepare-session --spec {specPath} --json";
        var output = await RunAsync("uv", ["run", "lamplighter-opencode", "prepare-session", "--spec", specPath, "--json"], session.RuntimePath, cancellationToken);
        if (output.ExitCode != 0)
        {
            throw new HarnessException("Harness prepare-session failed.", output.ToDiagnostics(command, session.RuntimePath));
        }

        return new PrepareSessionResult(output.ToDiagnostics(command, session.RuntimePath));
    }

    public async Task<AgentTurnResult> SubmitTurnAsync(AgentSessionRecord session, AgentTurnRecord turn, CancellationToken cancellationToken)
    {
        var requestPath = Path.Combine(session.RuntimePath, $"{turn.Id}.request.json");
        var request = AgentTurnRequest.FromTurn(turn);
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, JsonDefaults.Options), cancellationToken);

        var command = $"uv run lamplighter-opencode submit-turn --session {session.Id} --request {requestPath} --json";
        var output = await RunAsync(
            "uv",
            ["run", "lamplighter-opencode", "submit-turn", "--session", session.Id, "--request", requestPath, "--json"],
            session.RuntimePath,
            cancellationToken);
        if (output.ExitCode != 0)
        {
            throw new HarnessException("Harness submit-turn failed.", output.ToDiagnostics(command, session.RuntimePath));
        }

        var result = JsonSerializer.Deserialize<AgentTurnResult>(output.Stdout, JsonDefaults.Options)
            ?? throw new HarnessException("Harness returned an empty turn result.", output.ToDiagnostics(command, session.RuntimePath));
        return result with { Diagnostics = output.ToDiagnostics(command, session.RuntimePath) };
    }

    public Task CancelSessionAsync(AgentSessionRecord session, string reason, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private static async Task<ProcessOutput> RunAsync(string fileName, string[] arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var repoRoot = LocateHarnessRepo();
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessOutput(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string LocateHarnessRepo()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(Path.Combine(current, "pyproject.toml")) && Directory.Exists(Path.Combine(current, "src", "lamplighter_opencode")))
            {
                return current;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent == current)
            {
                break;
            }
            current = parent ?? "";
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    }
}

public sealed class HarnessException(string message, HarnessDiagnostics diagnostics) : Exception(message)
{
    public HarnessDiagnostics Diagnostics { get; } = diagnostics;
}

public sealed class AgentSessionStore
{
    private readonly ConcurrentDictionary<string, AgentSessionRecord> _sessions = new();
    private readonly ConcurrentDictionary<string, AgentTurnRecord> _turns = new();
    private readonly ConcurrentDictionary<string, ConcurrentBag<RuntimeEventRecord>> _events = new();

    public async Task UpsertSessionAsync(AgentSessionRecord session, CancellationToken cancellationToken)
    {
        _sessions[session.Id] = session;
        Directory.CreateDirectory(session.RuntimePath);
        await WriteJsonAsync(Path.Combine(session.RuntimePath, "session.json"), session, cancellationToken);
    }

    public Task<AgentSessionRecord?> GetSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return Task.FromResult(session);
    }

    public async Task UpsertInventorySessionAsync(
        string runnerId,
        RunnerAgentInventoryItem agent,
        CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(agent.AgentSessionId, out var existing))
        {
            var normalizedStatus = NormalizeAgentStatus(agent.Status);
            var preserveStatus = IsTerminalSessionStatus(existing.Status);
            var updated = existing with
            {
                ControllerId = runnerId,
                Status = preserveStatus ? existing.Status : normalizedStatus,
                ReadyAt = !preserveStatus && normalizedStatus == "ready"
                    ? existing.ReadyAt ?? agent.ObservedAt
                    : existing.ReadyAt,
                RuntimePath = agent.RuntimePath,
                WorkspacePath = agent.WorkspacePath
            };
            await UpsertSessionAsync(updated, cancellationToken);
            return;
        }

        await UpsertSessionAsync(AgentSessionRecord.FromInventory(runnerId, agent), cancellationToken);
    }

    public async Task UpsertTurnAsync(AgentTurnRecord turn, CancellationToken cancellationToken)
    {
        _turns[TurnKey(turn.SessionId, turn.Id)] = turn;
        var sessionRoot = _sessions.TryGetValue(turn.SessionId, out var session)
            ? session.RuntimePath
            : RuntimePaths.Session(turn.SessionId);
        var path = Path.Combine(sessionRoot, "turns", $"{turn.Id}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await WriteJsonAsync(path, turn, cancellationToken);
    }

    public Task<AgentTurnRecord?> GetTurnAsync(string sessionId, string turnId, CancellationToken cancellationToken)
    {
        _turns.TryGetValue(TurnKey(sessionId, turnId), out var turn);
        return Task.FromResult(turn);
    }

    public Task<IReadOnlyCollection<AgentTurnRecord>> GetTurnsAsync(string sessionId, CancellationToken cancellationToken)
    {
        var turns = _turns.Values
            .Where(turn => turn.SessionId == sessionId)
            .OrderBy(turn => turn.CreatedAt)
            .ToArray();
        return Task.FromResult<IReadOnlyCollection<AgentTurnRecord>>(turns);
    }

    public async Task AddEventAsync(RuntimeEventRecord runtimeEvent, CancellationToken cancellationToken)
    {
        var events = _events.GetOrAdd(runtimeEvent.SessionId, _ => []);
        events.Add(runtimeEvent);
        var path = Path.Combine(RuntimePaths.Session(runtimeEvent.SessionId), "events.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.AppendAllTextAsync(path, JsonSerializer.Serialize(runtimeEvent, JsonDefaults.Options) + Environment.NewLine, cancellationToken);
    }

    public Task<IReadOnlyCollection<RuntimeEventRecord>> GetEventsAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!_events.TryGetValue(sessionId, out var events))
        {
            return Task.FromResult<IReadOnlyCollection<RuntimeEventRecord>>([]);
        }

        return Task.FromResult<IReadOnlyCollection<RuntimeEventRecord>>(events.OrderBy(e => e.CreatedAt).ToArray());
    }

    private static string TurnKey(string sessionId, string turnId) => $"{sessionId}:{turnId}";

    private static string NormalizeAgentStatus(string status)
    {
        return status switch
        {
            "planned" or "starting" => "preparing",
            _ => status
        };
    }

    private static bool IsTerminalSessionStatus(string status)
    {
        return status is "cancelled" or "cancelling" or "failed";
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonDefaults.Options), cancellationToken);
    }
}

public sealed class RunnerControlStore
{
    private readonly ConcurrentDictionary<string, RunnerCommandEnvelope> _commands = new();
    private readonly ConcurrentBag<RunnerEventEnvelope> _events = [];
    private readonly ConcurrentDictionary<string, StoredClaimCheckContent> _content = new();
    private readonly ConcurrentDictionary<string, RunnerHeartbeat> _runnerHeartbeats = new();
    private readonly string _runtimeRoot;

    public RunnerControlStore() : this(RuntimePaths.Root)
    {
    }

    public RunnerControlStore(string runtimeRoot)
    {
        _runtimeRoot = runtimeRoot;
        LoadCommands();
        LoadEvents();
        LoadContentMetadata();
        LoadRunnerHeartbeats();
    }

    public Task EnqueueCommandAsync(RunnerCommandEnvelope command, CancellationToken cancellationToken)
    {
        _commands[command.Id] = command;
        return WriteJsonAsync(CommandPath(command.Id), command, cancellationToken);
    }

    public Task<IReadOnlyCollection<RunnerCommandEnvelope>> GetAvailableCommandsAsync(string runnerId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var commands = _commands.Values
            .Where(command => command.Status == RunnerCommandStatuses.Pending)
            .Where(command => command.AvailableAt <= now)
            .Where(command => command.RunnerId is null || command.RunnerId == runnerId)
            .Where(command => command.Lease is null || command.Lease.ExpiresAt <= now)
            .OrderBy(command => command.CreatedAt)
            .ToArray();
        return Task.FromResult<IReadOnlyCollection<RunnerCommandEnvelope>>(commands);
    }

    public async Task<RunnerCommandMutationResult> ClaimCommandAsync(
        string commandId,
        ClaimRunnerCommandRequest request,
        CancellationToken cancellationToken)
    {
        if (!_commands.TryGetValue(commandId, out var command))
        {
            return RunnerCommandMutationResult.NotFound();
        }

        var now = DateTimeOffset.UtcNow;
        if (command.Lease is not null && command.Lease.ExpiresAt > now && command.Lease.RunnerId != request.RunnerId)
        {
            return RunnerCommandMutationResult.Conflict("Command is leased by another runner.");
        }

        if (command.Status is RunnerCommandStatuses.Completed or RunnerCommandStatuses.Cancelled)
        {
            return RunnerCommandMutationResult.Conflict($"Command is already {command.Status}.");
        }

        var lease = new RunnerCommandLease(
            LeaseId: Ids.New("lease"),
            RunnerId: request.RunnerId,
            ClaimedAt: now,
            ExpiresAt: now.AddSeconds(Math.Clamp(request.LeaseSeconds, 1, 3600)),
            Attempt: (command.Lease?.Attempt ?? 0) + 1);
        var claimed = command with
        {
            RunnerId = request.RunnerId,
            Status = RunnerCommandStatuses.Claimed,
            Lease = lease
        };
        _commands[commandId] = claimed;
        await WriteJsonAsync(CommandPath(commandId), claimed, cancellationToken);
        return RunnerCommandMutationResult.Ok(claimed);
    }

    public async Task<RunnerCommandMutationResult> CompleteCommandAsync(
        string commandId,
        CompleteRunnerCommandRequest request,
        CancellationToken cancellationToken)
    {
        if (!_commands.TryGetValue(commandId, out var command))
        {
            return RunnerCommandMutationResult.NotFound();
        }

        if (command.Lease is null || command.Lease.LeaseId != request.LeaseId || command.Lease.RunnerId != request.RunnerId)
        {
            return RunnerCommandMutationResult.Conflict("Command lease does not match completion request.");
        }

        var completed = command with
        {
            Status = request.Status,
            Lease = null
        };
        _commands[commandId] = completed;
        await WriteJsonAsync(CommandPath(commandId), completed, cancellationToken);
        return RunnerCommandMutationResult.Ok(completed);
    }

    public async Task AddEventAsync(RunnerEventEnvelope runnerEvent, CancellationToken cancellationToken)
    {
        _events.Add(runnerEvent);
        var path = RunnerEventsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.AppendAllTextAsync(
            path,
            JsonSerializer.Serialize(runnerEvent, JsonLineOptions) + Environment.NewLine,
            cancellationToken);
    }

    public Task<IReadOnlyCollection<RunnerEventEnvelope>> GetEventsAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyCollection<RunnerEventEnvelope>>(_events.OrderBy(e => e.CreatedAt).ToArray());
    }

    public async Task<ClaimCheckContentUploadResponse> SaveContentAsync(
        ClaimCheckContentUploadRequest request,
        CancellationToken cancellationToken)
    {
        var bytes = Convert.FromBase64String(request.ContentBase64);
        var actualSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(actualSha, request.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Content sha256 does not match uploaded bytes.");
        }

        if (bytes.LongLength != request.Length)
        {
            throw new InvalidDataException("Content length does not match uploaded bytes.");
        }

        var contentId = Ids.New("content");
        var contentRef = new ClaimCheckContentRef(
            Uri: $"tradecraft://content/{contentId}",
            Sha256: actualSha,
            ContentType: request.ContentType,
            Length: bytes.LongLength);
        _content[contentId] = new StoredClaimCheckContent(contentRef, bytes);
        Directory.CreateDirectory(ContentRoot());
        await File.WriteAllBytesAsync(ContentBlobPath(contentId), bytes, cancellationToken);
        await WriteJsonAsync(ContentMetadataPath(contentId), contentRef, cancellationToken);
        return new ClaimCheckContentUploadResponse(contentRef);
    }

    public async Task<ClaimCheckContentRef> SaveJsonContentAsync<T>(
        T value,
        string contentType,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, RunnerProtocolJson.Options);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var upload = new ClaimCheckContentUploadRequest(
            ContentType: contentType,
            Sha256: sha,
            Length: bytes.LongLength,
            ContentBase64: Convert.ToBase64String(bytes));
        var response = await SaveContentAsync(upload, cancellationToken);
        return response.ContentRef;
    }

    public async Task<StoredClaimCheckContent?> GetContentAsync(string contentId, CancellationToken cancellationToken)
    {
        if (_content.TryGetValue(contentId, out var content))
        {
            return content;
        }

        var metadataPath = ContentMetadataPath(contentId);
        var blobPath = ContentBlobPath(contentId);
        if (!File.Exists(metadataPath) || !File.Exists(blobPath))
        {
            return null;
        }

        var contentRef = await ReadJsonAsync<ClaimCheckContentRef>(metadataPath, cancellationToken);
        var bytes = await File.ReadAllBytesAsync(blobPath, cancellationToken);
        content = new StoredClaimCheckContent(contentRef, bytes);
        _content[contentId] = content;
        return content;
    }

    public Task UpsertRunnerHeartbeatAsync(RunnerHeartbeat heartbeat, CancellationToken cancellationToken)
    {
        _runnerHeartbeats[heartbeat.RunnerId] = heartbeat;
        return WriteJsonAsync(RunnerHeartbeatPath(heartbeat.RunnerId), heartbeat, cancellationToken);
    }

    public Task<RunnerHeartbeat?> GetRunnerHeartbeatAsync(string runnerId, CancellationToken cancellationToken)
    {
        _runnerHeartbeats.TryGetValue(runnerId, out var heartbeat);
        return Task.FromResult(heartbeat);
    }

    public Task<IReadOnlyCollection<RunnerHeartbeat>> GetRunnerHeartbeatsAsync(CancellationToken cancellationToken)
    {
        var heartbeats = _runnerHeartbeats.Values
            .OrderBy(heartbeat => heartbeat.RunnerId, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult<IReadOnlyCollection<RunnerHeartbeat>>(heartbeats);
    }

    private void LoadCommands()
    {
        var root = CommandRoot();
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*.json"))
        {
            var command = JsonSerializer.Deserialize<RunnerCommandEnvelope>(File.ReadAllText(path), RunnerProtocolJson.Options);
            if (command is not null)
            {
                _commands[command.Id] = command;
            }
        }
    }

    private void LoadEvents()
    {
        var path = RunnerEventsPath();
        if (!File.Exists(path))
        {
            return;
        }

        foreach (var line in File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            var runnerEvent = JsonSerializer.Deserialize<RunnerEventEnvelope>(line, RunnerProtocolJson.Options);
            if (runnerEvent is not null)
            {
                _events.Add(runnerEvent);
            }
        }
    }

    private void LoadContentMetadata()
    {
        var root = ContentRoot();
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*.json"))
        {
            var contentId = Path.GetFileNameWithoutExtension(path);
            var blobPath = ContentBlobPath(contentId);
            var contentRef = JsonSerializer.Deserialize<ClaimCheckContentRef>(File.ReadAllText(path), RunnerProtocolJson.Options);
            if (contentRef is not null && File.Exists(blobPath))
            {
                _content[contentId] = new StoredClaimCheckContent(contentRef, File.ReadAllBytes(blobPath));
            }
        }
    }

    private void LoadRunnerHeartbeats()
    {
        var root = RunnerHeartbeatRoot();
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*.json"))
        {
            var heartbeat = JsonSerializer.Deserialize<RunnerHeartbeat>(File.ReadAllText(path), RunnerProtocolJson.Options);
            if (heartbeat is not null)
            {
                _runnerHeartbeats[heartbeat.RunnerId] = heartbeat;
            }
        }
    }

    private string CommandRoot() => Path.Combine(_runtimeRoot, "runner", "commands");
    private string CommandPath(string commandId) => Path.Combine(CommandRoot(), $"{commandId}.json");
    private string RunnerEventsPath() => Path.Combine(_runtimeRoot, "runner", "events.jsonl");
    private string RunnerHeartbeatRoot() => Path.Combine(_runtimeRoot, "runner", "runners");
    private string RunnerHeartbeatPath(string runnerId) => Path.Combine(RunnerHeartbeatRoot(), $"{runnerId}.json");
    private string ContentRoot() => Path.Combine(_runtimeRoot, "content");
    private string ContentBlobPath(string contentId) => Path.Combine(ContentRoot(), $"{contentId}.bin");
    private string ContentMetadataPath(string contentId) => Path.Combine(ContentRoot(), $"{contentId}.json");

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, RunnerProtocolJson.Options), cancellationToken);
    }

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<T>(json, RunnerProtocolJson.Options)
            ?? throw new InvalidDataException($"{path} did not contain a valid {typeof(T).Name}.");
    }

    private static JsonSerializerOptions JsonLineOptions { get; } = new(RunnerProtocolJson.Options)
    {
        WriteIndented = false
    };
}

public enum RunnerCommandMutationStatus
{
    Ok,
    NotFound,
    Conflict
}

public sealed record RunnerCommandMutationResult(
    RunnerCommandMutationStatus Status,
    RunnerCommandEnvelope? Command,
    string? Message)
{
    public static RunnerCommandMutationResult Ok(RunnerCommandEnvelope command) => new(RunnerCommandMutationStatus.Ok, command, null);
    public static RunnerCommandMutationResult NotFound() => new(RunnerCommandMutationStatus.NotFound, null, null);
    public static RunnerCommandMutationResult Conflict(string message) => new(RunnerCommandMutationStatus.Conflict, null, message);
}

public readonly record struct StoredClaimCheckContent(ClaimCheckContentRef ContentRef, byte[] Bytes)
{
    public string ContentType => ContentRef.ContentType;
}

public static class RuntimePaths
{
    public static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.agent-runtime"));
    public static string Session(string sessionId) => Path.Combine(Root, "sessions", sessionId);
}

public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

public static class Ids
{
    public static string New(string prefix) => $"{prefix}_{Guid.NewGuid():N}";
}

public static class RunnerCommandFactory
{
    public static RunnerCommandEnvelope Create(
        string agentSessionId,
        string type,
        ClaimCheckContentRef payloadRef,
        string correlationId,
        string idempotencyKey,
        string? runnerId = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new RunnerCommandEnvelope(
            Id: Ids.New("cmd"),
            RunnerId: runnerId,
            AgentSessionId: agentSessionId,
            Type: type,
            Status: RunnerCommandStatuses.Pending,
            PayloadRef: payloadRef,
            CorrelationId: correlationId,
            IdempotencyKey: idempotencyKey,
            CreatedAt: now,
            AvailableAt: now,
            Lease: null);
    }
}

public sealed record CreateAgentSessionRequest(string? Goal = null, string? BranchName = null, string? ControllerId = null);
public sealed record SubmitTurnRequest(string Prompt);
public sealed record CancelSessionRequest(string? Reason);

public sealed record AgentControllerRecord(
    string RunnerId,
    string Status,
    DateTimeOffset ObservedAt,
    IReadOnlyList<AgentControllerAgentRecord> Agents)
{
    public static AgentControllerRecord FromHeartbeat(RunnerHeartbeat heartbeat)
    {
        return new AgentControllerRecord(
            RunnerId: heartbeat.RunnerId,
            Status: heartbeat.Status,
            ObservedAt: heartbeat.ObservedAt,
            Agents: heartbeat.Agents.Select(AgentControllerAgentRecord.FromInventory).ToArray());
    }
}

public sealed record AgentControllerAgentRecord(
    string AgentSessionId,
    string Status,
    string RuntimePath,
    string WorkspacePath,
    string? OpenCodeEndpoint,
    int? OpenCodePid,
    DateTimeOffset ObservedAt)
{
    public static AgentControllerAgentRecord FromInventory(RunnerAgentInventoryItem agent)
    {
        return new AgentControllerAgentRecord(
            AgentSessionId: agent.AgentSessionId,
            Status: agent.Status,
            RuntimePath: agent.RuntimePath,
            WorkspacePath: agent.WorkspacePath,
            OpenCodeEndpoint: agent.OpenCodeEndpoint,
            OpenCodePid: agent.OpenCodePid,
            ObservedAt: agent.ObservedAt);
    }
}

public sealed record AgentSessionRecord(
    string Id,
    string? ControllerId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadyAt,
    DateTimeOffset? FailedAt,
    DateTimeOffset? EndedAt,
    string RuntimePath,
    string WorkspacePath,
    string BackendKind,
    string BranchName,
    string? FailureSummary,
    HarnessDiagnostics? Diagnostics)
{
    public static AgentSessionRecord Create(CreateAgentSessionRequest request, string runtimeRoot)
    {
        var id = Ids.New("session");
        var runtimePath = Path.Combine(runtimeRoot, "sessions", id);
        return new AgentSessionRecord(
            Id: id,
            ControllerId: request.ControllerId,
            Status: "preparing",
            CreatedAt: DateTimeOffset.UtcNow,
            ReadyAt: null,
            FailedAt: null,
            EndedAt: null,
            RuntimePath: runtimePath,
            WorkspacePath: Path.Combine(runtimePath, "workspace"),
            BackendKind: "opencode",
            BranchName: request.BranchName ?? "poc-chat-through-harness",
            FailureSummary: null,
            Diagnostics: null);
    }

    public static AgentSessionRecord FromInventory(string controllerId, RunnerAgentInventoryItem agent)
    {
        var status = agent.Status switch
        {
            "planned" or "starting" => "preparing",
            _ => agent.Status
        };
        return new AgentSessionRecord(
            Id: agent.AgentSessionId,
            ControllerId: controllerId,
            Status: status,
            CreatedAt: agent.ObservedAt,
            ReadyAt: status == "ready" ? agent.ObservedAt : null,
            FailedAt: status == "failed" ? agent.ObservedAt : null,
            EndedAt: null,
            RuntimePath: agent.RuntimePath,
            WorkspacePath: agent.WorkspacePath,
            BackendKind: "opencode",
            BranchName: "poc-chat-through-harness",
            FailureSummary: null,
            Diagnostics: null);
    }
}

public sealed record AgentTurnRecord(
    string Id,
    string SessionId,
    string Type,
    string Prompt,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? Response,
    string? FailureSummary,
    string? FailureDetail,
    HarnessDiagnostics? Diagnostics)
{
    public static AgentTurnRecord Create(string sessionId, string prompt)
    {
        return new AgentTurnRecord(
            Id: Ids.New("turn"),
            SessionId: sessionId,
            Type: "prompt_response",
            Prompt: prompt,
            Status: "submitted",
            CreatedAt: DateTimeOffset.UtcNow,
            CompletedAt: null,
            Response: null,
            FailureSummary: null,
            FailureDetail: null,
            Diagnostics: null);
    }
}

public sealed record RuntimeEventRecord(
    string Id,
    string SessionId,
    string? TurnId,
    string Type,
    DateTimeOffset CreatedAt,
    object Payload)
{
    public static RuntimeEventRecord Create(string sessionId, string? turnId, string type, object payload)
    {
        return new RuntimeEventRecord(Ids.New("event"), sessionId, turnId, type, DateTimeOffset.UtcNow, payload);
    }
}

public sealed record HarnessDiagnostics(string Command, int ExitCode, string StdoutTail, string StderrTail, string LogPath);
public sealed record PrepareSessionResult(HarnessDiagnostics Diagnostics);
public sealed record FailureReport(
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("detail")] string? Detail);

public sealed record AgentTurnResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("agent_session_id")] string AgentSessionId,
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("artifact_refs")] IReadOnlyList<string> ArtifactRefs,
    [property: JsonPropertyName("changed_files")] IReadOnlyList<string> ChangedFiles,
    [property: JsonPropertyName("commands_observed")] IReadOnlyList<string> CommandsObserved,
    [property: JsonPropertyName("failure_report")] FailureReport? FailureReport,
    HarnessDiagnostics? Diagnostics);

public sealed record AgentSessionSpec(
    [property: JsonPropertyName("agent_session_id")] string AgentSessionId,
    [property: JsonPropertyName("goal_run_id")] string GoalRunId,
    [property: JsonPropertyName("phase_run_id")] string PhaseRunId,
    [property: JsonPropertyName("agent_definition_id")] string AgentDefinitionId,
    [property: JsonPropertyName("workspace_ref")] string WorkspaceRef,
    [property: JsonPropertyName("repo_ref")] string RepoRef,
    [property: JsonPropertyName("branch_name")] string BranchName,
    [property: JsonPropertyName("backend")] BackendSpec Backend,
    [property: JsonPropertyName("communication_channel")] CommunicationChannelSpec CommunicationChannel,
    [property: JsonPropertyName("artifact_contract")] ArtifactContractSpec ArtifactContract,
    [property: JsonPropertyName("timeout_policy")] TimeoutPolicySpec TimeoutPolicy)
{
    public static AgentSessionSpec FromSession(AgentSessionRecord session)
    {
        return new AgentSessionSpec(
            AgentSessionId: session.Id,
            GoalRunId: "chat_poc",
            PhaseRunId: "prompt_response",
            AgentDefinitionId: "opencode.default",
            WorkspaceRef: session.WorkspacePath,
            RepoRef: "lamplighter-opencode",
            BranchName: session.BranchName,
            Backend: new BackendSpec("opencode", new BackendServerSpec("127.0.0.1", 4096)),
            CommunicationChannel: new CommunicationChannelSpec("jsonl_stdio"),
            ArtifactContract: new ArtifactContractSpec(Path.Combine(session.RuntimePath, "artifacts")),
            TimeoutPolicy: new TimeoutPolicySpec(60, 300));
    }
}

public sealed record BackendSpec(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("server")] BackendServerSpec Server);
public sealed record BackendServerSpec(
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port);
public sealed record CommunicationChannelSpec([property: JsonPropertyName("kind")] string Kind);
public sealed record ArtifactContractSpec([property: JsonPropertyName("root")] string Root);
public sealed record TimeoutPolicySpec(
    [property: JsonPropertyName("prepare_seconds")] int PrepareSeconds,
    [property: JsonPropertyName("turn_seconds")] int TurnSeconds);

public sealed record AgentTurnRequest(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("agent_session_id")] string AgentSessionId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("instruction")] string Instruction,
    [property: JsonPropertyName("correlation_id")] string CorrelationId)
{
    public static AgentTurnRequest FromTurn(AgentTurnRecord turn)
    {
        return new AgentTurnRequest(turn.Id, turn.SessionId, turn.Type, turn.Prompt, turn.Id);
    }
}

public sealed record ProcessOutput(int ExitCode, string Stdout, string Stderr)
{
    public HarnessDiagnostics ToDiagnostics(string command, string logPath)
    {
        return new HarnessDiagnostics(command, ExitCode, Tail(Stdout), Tail(Stderr), logPath);
    }

    private static string Tail(string value)
    {
        const int max = 4000;
        return value.Length <= max ? value : value[^max..];
    }
}
