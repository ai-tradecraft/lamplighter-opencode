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
builder.Services.AddSingleton<AgentStore>();
builder.Services.AddSingleton<AgentSessionStore>();
builder.Services.AddSingleton<RunnerControlStore>();
builder.Services.AddSingleton<CentralLogStore>();
builder.Services.AddSingleton<HarnessClientFactory>();
builder.Services.AddSingleton<IHarnessClient>(sp => sp.GetRequiredService<HarnessClientFactory>().Create());

var app = builder.Build();

app.UseCors();
app.MapHub<AgentSessionHub>("/hubs/agent-sessions");

app.MapPost("/api/client-logs", async (
    ClientLogRequest request,
    CentralLogStore logStore,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Message))
    {
        return Results.BadRequest(new { message = "Log message is required." });
    }

    await logStore.WriteBrowserLogAsync(request, cancellationToken);
    return Results.Accepted();
});

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
    AgentStore agentStore,
    AgentSessionStore sessionStore,
    IHubContext<AgentSessionHub> hub,
    CancellationToken cancellationToken) =>
{
    await runnerStore.AddEventAsync(runnerEvent, cancellationToken);
    await ApplyRunnerEventAsync(runnerEvent, runnerStore, agentStore, sessionStore, cancellationToken);
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
    AgentStore agentStore,
    AgentSessionStore sessionStore,
    CancellationToken cancellationToken) =>
{
    await runnerStore.UpsertRunnerHeartbeatAsync(heartbeat, cancellationToken);
    foreach (var agent in heartbeat.Agents)
    {
        await agentStore.UpsertInventoryAgentAsync(heartbeat.RunnerId, agent, cancellationToken);
        foreach (var session in agent.Sessions ?? [])
        {
            await sessionStore.UpsertInventorySessionAsync(
                heartbeat.RunnerId,
                agent.AgentId ?? agent.AgentSessionId,
                session,
                agent.WorkspacePath,
                cancellationToken);
        }
        if (agent.AgentId is null)
        {
            await sessionStore.UpsertInventorySessionAsync(heartbeat.RunnerId, agent, cancellationToken);
        }
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
    return Results.Ok(
        heartbeats
            .Where(AgentControllerRecord.IsActive)
            .Select(AgentControllerRecord.FromHeartbeat)
            .ToArray());
});

app.MapGet("/api/agent-controllers/{runnerId}", async (
    string runnerId,
    RunnerControlStore runnerStore,
    CancellationToken cancellationToken) =>
{
    var heartbeat = await runnerStore.GetRunnerHeartbeatAsync(runnerId, cancellationToken);
    return heartbeat is null || !AgentControllerRecord.IsActive(heartbeat)
        ? Results.NotFound()
        : Results.Ok(AgentControllerRecord.FromHeartbeat(heartbeat));
});

app.MapPost("/api/agent-controllers/{controllerId}/agents", async (
    string controllerId,
    CreateAgentRequest request,
    AgentStore agentStore,
    RunnerControlStore runnerStore,
    CancellationToken cancellationToken) =>
{
    var heartbeat = await runnerStore.GetRunnerHeartbeatAsync(controllerId, cancellationToken);
    if (heartbeat is null || !AgentControllerRecord.IsActive(heartbeat))
    {
        return Results.Conflict(new { message = $"Agent controller {controllerId} is not active." });
    }

    var agent = AgentRecord.Create(controllerId, request);
    await agentStore.UpsertAgentAsync(agent, cancellationToken);
    var payloadRef = await runnerStore.SaveJsonContentAsync(
        AgentSpec.FromAgent(agent),
        "application/vnd.tradecraft.agent-spec+json",
        cancellationToken);
    var command = RunnerCommandFactory.Create(
        agent.Id,
        RunnerCommandTypes.PrepareAgent,
        payloadRef,
        correlationId: agent.Id,
        idempotencyKey: agent.Id,
        runnerId: controllerId,
        agentId: agent.Id);
    await runnerStore.EnqueueCommandAsync(command, cancellationToken);
    return Results.Created($"/api/agents/{agent.Id}", agent);
});

app.MapGet("/api/agents/{agentId}", async (
    string agentId,
    AgentStore agentStore,
    CancellationToken cancellationToken) =>
{
    var agent = await agentStore.GetAgentAsync(agentId, cancellationToken);
    return agent is null ? Results.NotFound() : Results.Ok(agent);
});

app.MapPost("/api/agents/{agentId}/stop", async (
    string agentId,
    AgentStore agentStore,
    RunnerControlStore runnerStore,
    CancellationToken cancellationToken) =>
{
    var agent = await agentStore.GetAgentAsync(agentId, cancellationToken);
    if (agent is null)
    {
        return Results.NotFound();
    }
    var payloadRef = await runnerStore.SaveJsonContentAsync(
        new { reason = "Stopped by operator." },
        "application/vnd.tradecraft.agent-stop+json",
        cancellationToken);
    var command = RunnerCommandFactory.Create(
        agent.Id,
        RunnerCommandTypes.StopAgent,
        payloadRef,
        correlationId: agent.Id,
        idempotencyKey: $"stop:{agent.Id}",
        runnerId: agent.ControllerId,
        agentId: agent.Id);
    await runnerStore.EnqueueCommandAsync(command, cancellationToken);
    agent = agent with { Status = "stopping" };
    await agentStore.UpsertAgentAsync(agent, cancellationToken);
    return Results.Ok(agent);
});

app.MapGet("/api/agents/{agentId}/sessions", async (
    string agentId,
    AgentSessionStore store,
    CancellationToken cancellationToken) =>
{
    return Results.Ok(await store.GetSessionsByAgentAsync(agentId, cancellationToken));
});

app.MapPost("/api/agents/{agentId}/sessions", async (
    string agentId,
    CreateAgentSessionRequest request,
    AgentStore agentStore,
    AgentSessionStore sessionStore,
    RunnerControlStore runnerStore,
    IHubContext<AgentSessionHub> hub,
    CancellationToken cancellationToken) =>
{
    var agent = await agentStore.GetAgentAsync(agentId, cancellationToken);
    if (agent is null)
    {
        return Results.NotFound();
    }
    if (agent.Status is not ("ready" or "planned"))
    {
        return Results.Conflict(new { message = $"Agent {agentId} is {agent.Status}." });
    }

    var session = AgentSessionRecord.CreateForAgent(agent, request);
    await sessionStore.UpsertSessionAsync(session, cancellationToken);
    await PublishAsync(sessionStore, hub, session.Id, null, "agent_session.preparing", new { session.Id, session.AgentId }, cancellationToken);
    var payloadRef = await runnerStore.SaveJsonContentAsync(
        AgentChatSessionSpec.FromSession(session),
        "application/vnd.tradecraft.agent-chat-session-spec+json",
        cancellationToken);
    var command = RunnerCommandFactory.Create(
        session.Id,
        RunnerCommandTypes.CreateAgentSession,
        payloadRef,
        correlationId: session.Id,
        idempotencyKey: session.Id,
        runnerId: agent.ControllerId,
        agentId: agent.Id,
        sessionId: session.Id);
    await runnerStore.EnqueueCommandAsync(command, cancellationToken);
    await PublishAsync(sessionStore, hub, session.Id, null, "agent_session.command_queued", command, cancellationToken);
    return Results.Created($"/api/agent-sessions/{session.Id}", session);
});

app.MapPost("/api/agent-sessions", async (
    CreateAgentSessionRequest request,
    AgentSessionStore store,
    RunnerControlStore runnerStore,
    IHubContext<AgentSessionHub> hub,
    CancellationToken cancellationToken) =>
{
    if (!string.IsNullOrWhiteSpace(request.ControllerId))
    {
        var heartbeat = await runnerStore.GetRunnerHeartbeatAsync(request.ControllerId, cancellationToken);
        if (heartbeat is null || !AgentControllerRecord.IsActive(heartbeat))
        {
            return Results.Conflict(new
            {
                message = $"Agent controller {request.ControllerId} is not active."
            });
        }
    }

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

    var turnRequest = AgentTurnRequest.FromTurn(turn, session.AgentId);
    var payloadRef = await runnerStore.SaveJsonContentAsync(turnRequest, "application/vnd.tradecraft.agent-turn-request+json", cancellationToken);
    var command = RunnerCommandFactory.Create(
        sessionId,
        RunnerCommandTypes.SubmitAgentTurn,
        payloadRef,
        correlationId: turn.Id,
        idempotencyKey: turn.Id,
        runnerId: session.ControllerId,
        agentId: session.AgentId,
        sessionId: session.Id);
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
        idempotencyKey: $"cancel:{sessionId}",
        runnerId: session.ControllerId,
        agentId: session.AgentId,
        sessionId: session.Id);
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
    AgentStore agentStore,
    AgentSessionStore sessionStore,
    CancellationToken cancellationToken)
{
    switch (runnerEvent.Type)
    {
        case RunnerEventTypes.AgentReady:
        case RunnerEventTypes.AgentFailed:
        case RunnerEventTypes.AgentStopped:
        {
            if (runnerEvent.AgentId is null)
            {
                break;
            }
            var agent = await agentStore.GetAgentAsync(runnerEvent.AgentId, cancellationToken);
            if (agent is not null)
            {
                var status = runnerEvent.Type switch
                {
                    RunnerEventTypes.AgentReady => "ready",
                    RunnerEventTypes.AgentStopped => "stopped",
                    _ => "failed"
                };
                await agentStore.UpsertAgentAsync(
                    agent with
                    {
                        Status = status,
                        ReadyAt = status == "ready" ? runnerEvent.CreatedAt : agent.ReadyAt,
                        EndedAt = status is "stopped" or "failed" ? runnerEvent.CreatedAt : agent.EndedAt
                    },
                    cancellationToken);
            }
            break;
        }

        case RunnerEventTypes.AgentSessionCreated:
        case RunnerEventTypes.AgentSessionReady:
        {
            var sessionId = runnerEvent.SessionId ?? runnerEvent.AgentSessionId;
            var session = await sessionStore.GetSessionAsync(sessionId, cancellationToken);
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
            var sessionId = runnerEvent.SessionId ?? runnerEvent.AgentSessionId;
            var session = await sessionStore.GetSessionAsync(sessionId, cancellationToken);
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
            var sessionId = runnerEvent.SessionId ?? runnerEvent.AgentSessionId;
            var session = await sessionStore.GetSessionAsync(sessionId, cancellationToken);
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
            var turn = await sessionStore.GetTurnAsync(
                runnerEvent.SessionId ?? runnerEvent.AgentSessionId,
                runnerEvent.CorrelationId,
                cancellationToken);
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
            var turn = await sessionStore.GetTurnAsync(
                runnerEvent.SessionId ?? runnerEvent.AgentSessionId,
                runnerEvent.CorrelationId,
                cancellationToken);
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

public sealed class AgentStore
{
    private readonly ConcurrentDictionary<string, AgentRecord> _agents = new();

    public async Task UpsertAgentAsync(AgentRecord agent, CancellationToken cancellationToken)
    {
        _agents[agent.Id] = agent;
        var path = Path.Combine(RuntimePaths.Root, "web", "agents", $"{agent.Id}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(agent, JsonDefaults.Options),
            cancellationToken);
    }

    public Task<AgentRecord?> GetAgentAsync(string agentId, CancellationToken cancellationToken)
    {
        _agents.TryGetValue(agentId, out var agent);
        return Task.FromResult(agent);
    }

    public async Task UpsertInventoryAgentAsync(
        string runnerId,
        RunnerAgentInventoryItem inventory,
        CancellationToken cancellationToken)
    {
        var agentId = inventory.AgentId ?? inventory.AgentSessionId;
        var observed = AgentRecord.FromInventory(runnerId, inventory);
        if (_agents.TryGetValue(agentId, out var existing))
        {
            observed = observed with
            {
                CreatedAt = existing.CreatedAt,
                BranchName = existing.BranchName,
                ReadyAt = observed.Status == "ready"
                    ? existing.ReadyAt ?? inventory.ObservedAt
                    : existing.ReadyAt
            };
        }
        await UpsertAgentAsync(observed, cancellationToken);
    }
}

public sealed class AgentSessionStore
{
    private readonly ConcurrentDictionary<string, AgentSessionRecord> _sessions = new();
    private readonly ConcurrentDictionary<string, AgentTurnRecord> _turns = new();
    private readonly ConcurrentDictionary<string, ConcurrentBag<RuntimeEventRecord>> _events = new();

    public async Task UpsertSessionAsync(AgentSessionRecord session, CancellationToken cancellationToken)
    {
        _sessions[session.Id] = session;
        var persistenceRoot = RuntimePaths.Session(session.Id);
        Directory.CreateDirectory(persistenceRoot);
        await WriteJsonAsync(Path.Combine(persistenceRoot, "session.json"), session, cancellationToken);
    }

    public Task<AgentSessionRecord?> GetSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return Task.FromResult(session);
    }

    public Task<IReadOnlyCollection<AgentSessionRecord>> GetSessionsByAgentAsync(
        string agentId,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyCollection<AgentSessionRecord>>(
            _sessions.Values
                .Where(session => session.AgentId == agentId)
                .OrderByDescending(session => session.CreatedAt)
                .ToArray());
    }

    public async Task UpsertInventorySessionAsync(
        string runnerId,
        string agentId,
        RunnerAgentSessionInventoryItem inventory,
        string workspacePath,
        CancellationToken cancellationToken)
    {
        var status = NormalizeAgentStatus(inventory.Status);
        var observed = new AgentSessionRecord(
            Id: inventory.SessionId,
            ControllerId: runnerId,
            Status: status,
            CreatedAt: inventory.ObservedAt,
            ReadyAt: status == "ready" ? inventory.ObservedAt : null,
            FailedAt: status == "failed" ? inventory.ObservedAt : null,
            EndedAt: status is "cancelled" or "failed" ? inventory.ObservedAt : null,
            RuntimePath: inventory.RuntimePath,
            WorkspacePath: workspacePath,
            BackendKind: "opencode",
            BranchName: "poc-chat-through-harness",
            FailureSummary: null,
            Diagnostics: null,
            AgentId: agentId);
        if (_sessions.TryGetValue(inventory.SessionId, out var existing))
        {
            observed = observed with
            {
                CreatedAt = existing.CreatedAt,
                BranchName = existing.BranchName,
                ReadyAt = status == "ready" ? existing.ReadyAt ?? inventory.ObservedAt : existing.ReadyAt,
                Status = IsTerminalSessionStatus(existing.Status) ? existing.Status : status
            };
        }
        await UpsertSessionAsync(observed, cancellationToken);
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
        var sessionRoot = RuntimePaths.Session(turn.SessionId);
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

public sealed class CentralLogStore
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private static readonly JsonSerializerOptions LogJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task WriteBrowserLogAsync(
        ClientLogRequest request,
        CancellationToken cancellationToken)
    {
        var entry = new
        {
            timestamp = request.Timestamp ?? DateTimeOffset.UtcNow,
            source = "browser",
            level = Truncate(request.Level ?? "information", 32),
            message = Truncate(request.Message, 2000),
            context = request.Context?
                .Take(32)
                .ToDictionary(
                    item => Truncate(item.Key, 100),
                    item => item.Value is null ? null : Truncate(item.Value, 1000))
        };
        var path = Path.Combine(RuntimePaths.LogRoot, "browser.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(
                path,
                JsonSerializer.Serialize(entry, LogJsonOptions) + Environment.NewLine,
                cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }
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
    public static string Root
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("CHAT_THROUGH_HARNESS_RUNTIME_ROOT");
            return string.IsNullOrWhiteSpace(configured)
                ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.agent-runtime"))
                : Path.GetFullPath(configured);
        }
    }

    public static string LogRoot
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("CHAT_THROUGH_HARNESS_LOG_ROOT");
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Root, "logs")
                : Path.GetFullPath(configured);
        }
    }

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
        string? runnerId = null,
        string? agentId = null,
        string? sessionId = null)
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
            Lease: null,
            AgentId: agentId,
            SessionId: sessionId);
    }
}

public sealed record CreateAgentRequest(string? BranchName = null);
public sealed record CreateAgentSessionRequest(
    string? Goal = null,
    string? BranchName = null,
    string? ControllerId = null);
public sealed record SubmitTurnRequest(string Prompt);
public sealed record CancelSessionRequest(string? Reason);
public sealed record ClientLogRequest(
    string? Level,
    string Message,
    DateTimeOffset? Timestamp,
    IReadOnlyDictionary<string, string?>? Context);

public sealed record AgentControllerRecord(
    string RunnerId,
    string Status,
    DateTimeOffset ObservedAt,
    IReadOnlyList<AgentControllerAgentRecord> Agents)
{
    private static readonly TimeSpan ActiveHeartbeatWindow = TimeSpan.FromSeconds(30);

    public static bool IsActive(RunnerHeartbeat heartbeat)
    {
        return heartbeat.ObservedAt >= DateTimeOffset.UtcNow - ActiveHeartbeatWindow;
    }

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
    DateTimeOffset ObservedAt,
    string? AgentId = null,
    IReadOnlyList<RunnerAgentSessionInventoryItem>? Sessions = null)
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
            ObservedAt: agent.ObservedAt,
            AgentId: agent.AgentId,
            Sessions: agent.Sessions);
    }
}

public sealed record AgentRecord(
    string Id,
    string ControllerId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadyAt,
    DateTimeOffset? EndedAt,
    string RuntimePath,
    string WorkspacePath,
    string BackendKind,
    string BranchName,
    string? OpenCodeEndpoint,
    int? OpenCodePid)
{
    public static AgentRecord Create(string controllerId, CreateAgentRequest request)
    {
        return new AgentRecord(
            Id: Ids.New("agent"),
            ControllerId: controllerId,
            Status: "preparing",
            CreatedAt: DateTimeOffset.UtcNow,
            ReadyAt: null,
            EndedAt: null,
            RuntimePath: "",
            WorkspacePath: "",
            BackendKind: "opencode",
            BranchName: request.BranchName ?? "poc-chat-through-harness",
            OpenCodeEndpoint: null,
            OpenCodePid: null);
    }

    public static AgentRecord FromInventory(string controllerId, RunnerAgentInventoryItem inventory)
    {
        var status = inventory.Status is "planned" or "starting" ? "preparing" : inventory.Status;
        return new AgentRecord(
            Id: inventory.AgentId ?? inventory.AgentSessionId,
            ControllerId: controllerId,
            Status: status,
            CreatedAt: inventory.ObservedAt,
            ReadyAt: status == "ready" ? inventory.ObservedAt : null,
            EndedAt: status is "stopped" or "failed" ? inventory.ObservedAt : null,
            RuntimePath: inventory.RuntimePath,
            WorkspacePath: inventory.WorkspacePath,
            BackendKind: "opencode",
            BranchName: "poc-chat-through-harness",
            OpenCodeEndpoint: inventory.OpenCodeEndpoint,
            OpenCodePid: inventory.OpenCodePid);
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
    HarnessDiagnostics? Diagnostics,
    string? AgentId = null)
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

    public static AgentSessionRecord CreateForAgent(AgentRecord agent, CreateAgentSessionRequest request)
    {
        return new AgentSessionRecord(
            Id: Ids.New("session"),
            ControllerId: agent.ControllerId,
            Status: "preparing",
            CreatedAt: DateTimeOffset.UtcNow,
            ReadyAt: null,
            FailedAt: null,
            EndedAt: null,
            RuntimePath: "",
            WorkspacePath: agent.WorkspacePath,
            BackendKind: agent.BackendKind,
            BranchName: request.BranchName ?? agent.BranchName,
            FailureSummary: null,
            Diagnostics: null,
            AgentId: agent.Id);
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

public sealed record AgentSpec(
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("workspace_ref")] string WorkspaceRef,
    [property: JsonPropertyName("backend")] AgentBackendSpec Backend,
    [property: JsonPropertyName("agent_definition_id")] string AgentDefinitionId,
    [property: JsonPropertyName("repo_ref")] string RepoRef,
    [property: JsonPropertyName("branch_name")] string BranchName,
    [property: JsonPropertyName("tool_profile")] object ToolProfile,
    [property: JsonPropertyName("mcp_profile")] object McpProfile,
    [property: JsonPropertyName("telemetry")] object Telemetry)
{
    public static AgentSpec FromAgent(AgentRecord agent)
    {
        return new AgentSpec(
            AgentId: agent.Id,
            WorkspaceRef: $"controller://agents/{agent.Id}/workspace",
            Backend: new AgentBackendSpec(
                "opencode",
                new BackendServerSpec("127.0.0.1", 4096),
                new Dictionary<string, object?>
                {
                    ["provider"] = "azure",
                    ["model"] = "azure/{env:AZURE_OPENAI_DEPLOYMENT}",
                    ["wire_api"] = "responses"
                },
                []),
            AgentDefinitionId: "opencode.default",
            RepoRef: "lamplighter-opencode",
            BranchName: agent.BranchName,
            ToolProfile: new { },
            McpProfile: new { },
            Telemetry: new { });
    }
}

public sealed record AgentBackendSpec(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("server")] BackendServerSpec Server,
    [property: JsonPropertyName("config")] IReadOnlyDictionary<string, object?> Config,
    [property: JsonPropertyName("required_env_vars")] IReadOnlyList<string> RequiredEnvironmentVariables);

public sealed record AgentChatSessionSpec(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("context_package")] object ContextPackage,
    [property: JsonPropertyName("goal_run_id")] string GoalRunId,
    [property: JsonPropertyName("phase_run_id")] string PhaseRunId,
    [property: JsonPropertyName("artifact_contract")] object ArtifactContract,
    [property: JsonPropertyName("timeout_policy")] object TimeoutPolicy,
    [property: JsonPropertyName("telemetry")] object Telemetry)
{
    public static AgentChatSessionSpec FromSession(AgentSessionRecord session)
    {
        return new AgentChatSessionSpec(
            SessionId: session.Id,
            AgentId: session.AgentId ?? throw new InvalidOperationException("Session does not have an agent id."),
            ContextPackage: new { goal = "chat_poc", phase = "prompt_response" },
            GoalRunId: "chat_poc",
            PhaseRunId: "prompt_response",
            ArtifactContract: new { },
            TimeoutPolicy: new { turn_seconds = 300 },
            Telemetry: new { });
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
    [property: JsonPropertyName("correlation_id")] string CorrelationId,
    [property: JsonPropertyName("agent_id")] string? AgentId = null,
    [property: JsonPropertyName("session_id")] string? SessionId = null)
{
    public static AgentTurnRequest FromTurn(AgentTurnRecord turn, string? agentId = null)
    {
        return new AgentTurnRequest(
            turn.Id,
            turn.SessionId,
            turn.Type,
            turn.Prompt,
            turn.Id,
            agentId,
            turn.SessionId);
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
