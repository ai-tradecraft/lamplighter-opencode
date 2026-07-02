using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatThroughHarness.Protocol.V1;
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

app.MapGet("/api/system/info", () => Results.Ok(new
{
    service = "chat-through-harness-api",
    contractVersion = 2,
    capabilities = new[]
    {
        "controller-workspaces",
        "agents",
        "multi-session-agents"
    }
}));

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

app.MapGet("/api/v1/controllers/{controllerId}/commands", async (
    string controllerId,
    string? wait,
    RunnerControlStore runnerStore,
    CancellationToken cancellationToken) =>
{
    var deadline = DateTimeOffset.UtcNow + ParseLongPollWait(wait);
    while (!cancellationToken.IsCancellationRequested)
    {
        var commands = await runnerStore.GetAvailableCommandsAsync(controllerId, cancellationToken);
        if (commands.Count > 0 || DateTimeOffset.UtcNow >= deadline)
        {
            return Results.Ok(commands);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
    }

    return Results.Ok(Array.Empty<ControllerCommand>());
});

app.MapPost("/api/v1/controllers/{controllerId}/commands/{commandId}/acknowledgements", async (
    string controllerId,
    string commandId,
    ControllerCommandAcknowledgement acknowledgement,
    RunnerControlStore runnerStore,
    CancellationToken cancellationToken) =>
{
    if (!controllerId.Equals(acknowledgement.ControllerId, StringComparison.Ordinal)
        || !commandId.Equals(acknowledgement.CommandId, StringComparison.Ordinal))
    {
        return Results.BadRequest(new { message = "Route and acknowledgement identifiers must match." });
    }

    var result = await runnerStore.AcknowledgeCommandAsync(
        acknowledgement,
        cancellationToken);
    return result.Status switch
    {
        ControllerCommandMutationStatus.NotFound => Results.NotFound(),
        ControllerCommandMutationStatus.Conflict => Results.Conflict(new { message = result.Message }),
        _ => Results.Ok(result.Acknowledgement)
    };
});

app.MapPost("/api/v1/controllers/{controllerId}/commands/{commandId}/lease-renewals", async (
    string controllerId,
    string commandId,
    ControllerCommandLeaseRenewal renewal,
    RunnerControlStore runnerStore,
    CancellationToken cancellationToken) =>
{
    if (!controllerId.Equals(renewal.ControllerId, StringComparison.Ordinal)
        || !commandId.Equals(renewal.CommandId, StringComparison.Ordinal))
    {
        return Results.BadRequest(new { message = "Route and lease-renewal identifiers must match." });
    }

    var result = await runnerStore.RenewCommandLeaseAsync(renewal, cancellationToken);
    return result.Status switch
    {
        ControllerCommandMutationStatus.NotFound => Results.NotFound(),
        ControllerCommandMutationStatus.Conflict => Results.Conflict(new { message = result.Message }),
        _ => Results.Ok(result.Command?.Execution.Lease)
    };
});

app.MapPost("/api/v1/controllers/{controllerId}/commands/{commandId}/completion", async (
    string controllerId,
    string commandId,
    ControllerCommandCompletion completion,
    RunnerControlStore runnerStore,
    CancellationToken cancellationToken) =>
{
    if (!controllerId.Equals(completion.ControllerId, StringComparison.Ordinal)
        || !commandId.Equals(completion.CommandId, StringComparison.Ordinal))
    {
        return Results.BadRequest(new { message = "Route and completion identifiers must match." });
    }

    var result = await runnerStore.CompleteCommandAsync(completion, cancellationToken);
    return result.Status switch
    {
        ControllerCommandMutationStatus.NotFound => Results.NotFound(),
        ControllerCommandMutationStatus.Conflict => Results.Conflict(new { message = result.Message }),
        _ => Results.Ok(result.Completion)
    };
});

app.MapPost("/api/v1/controllers/{controllerId}/events", async (
    string controllerId,
    ControllerEvent controllerEvent,
    RunnerControlStore runnerStore,
    AgentStore agentStore,
    AgentSessionStore sessionStore,
    IHubContext<AgentSessionHub> hub,
    CancellationToken cancellationToken) =>
{
    if (!controllerId.Equals(controllerEvent.ControllerId, StringComparison.Ordinal))
    {
        return Results.BadRequest(new { message = "Route and event controller identifiers must match." });
    }

    var result = await runnerStore.AddEventAsync(controllerEvent, cancellationToken);
    if (result.Status != ControllerEventMutationStatus.Ok)
    {
        return result.Status switch
        {
            ControllerEventMutationStatus.NotFound => Results.NotFound(),
            _ => Results.Conflict(new { message = result.Message })
        };
    }

    await ApplyControllerEventAsync(
        controllerEvent,
        runnerStore,
        agentStore,
        sessionStore,
        cancellationToken);
    await PublishAsync(
        sessionStore,
        hub,
        controllerEvent.Target.AgentSessionId ?? controllerEvent.Aggregate.Id,
        ControllerEventInvocationId(controllerEvent),
        UiEventType(controllerEvent),
        controllerEvent,
        cancellationToken);
    return Results.Accepted();
});

app.MapPost("/api/v1/controllers/heartbeats", async (
    ControllerHeartbeat heartbeat,
    RunnerControlStore runnerStore,
    AgentStore agentStore,
    AgentSessionStore sessionStore,
    CancellationToken cancellationToken) =>
{
    await runnerStore.UpsertControllerHeartbeatAsync(heartbeat, cancellationToken);
    foreach (var agent in ControllerInventoryObservation.FromHeartbeat(heartbeat))
    {
        var ownershipAccepted = await agentStore.UpsertInventoryAgentAsync(
            heartbeat.ControllerId,
            agent,
            cancellationToken);
        foreach (var session in ownershipAccepted ? agent.Sessions ?? [] : [])
        {
            await sessionStore.UpsertInventorySessionAsync(
                heartbeat.ControllerId,
                agent.AgentId ?? agent.AgentSessionId,
                session,
                agent.WorkspacePath,
                cancellationToken);
        }
    }

    return Results.Accepted();
});

app.MapPost("/api/v1/controller-content", async (
    ControllerContentUploadRequest request,
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

app.MapGet("/api/v1/controller-content/{contentId}", async (
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
    var heartbeats = await runnerStore.GetControllerHeartbeatsAsync(cancellationToken);
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
    var heartbeat = await runnerStore.GetControllerHeartbeatAsync(runnerId, cancellationToken);
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
    var heartbeat = await runnerStore.GetControllerHeartbeatAsync(controllerId, cancellationToken);
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
    var command = ControllerCommandFactory.Create(
        ControllerCommandTypes.StartAgentRuntime,
        payloadRef,
        correlationId: agent.Id,
        idempotencyKey: agent.Id,
        runnerId: controllerId,
        agentId: agent.Id);
    var enqueue = await runnerStore.EnqueueCommandAsync(command, cancellationToken);
    if (enqueue.Status == ControllerCommandEnqueueStatus.Conflict)
    {
        return Results.Conflict(new { message = enqueue.Message });
    }

    command = enqueue.Command!;
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
    var command = ControllerCommandFactory.Create(
        ControllerCommandTypes.StopAgentRuntime,
        payloadRef,
        correlationId: agent.Id,
        idempotencyKey: $"stop:{agent.Id}",
        runnerId: agent.ControllerId,
        agentId: agent.Id);
    var enqueue = await runnerStore.EnqueueCommandAsync(command, cancellationToken);
    if (enqueue.Status == ControllerCommandEnqueueStatus.Conflict)
    {
        return Results.Conflict(new { message = enqueue.Message });
    }

    command = enqueue.Command!;
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
    var heartbeat = await runnerStore.GetControllerHeartbeatAsync(agent.ControllerId, cancellationToken);
    if (heartbeat is null || !AgentControllerRecord.IsActive(heartbeat))
    {
        return Results.Conflict(new { message = $"Agent controller {agent.ControllerId} is not active." });
    }

    var session = AgentSessionRecord.CreateForAgent(agent, request);
    await sessionStore.UpsertSessionAsync(session, cancellationToken);
    await PublishAsync(sessionStore, hub, session.Id, null, "agent_session.preparing", new { session.Id, session.AgentId }, cancellationToken);
    var payloadRef = await runnerStore.SaveJsonContentAsync(
        AgentChatSessionSpec.FromSession(session),
        "application/vnd.tradecraft.agent-chat-session-spec+json",
        cancellationToken);
    var command = ControllerCommandFactory.Create(
        ControllerCommandTypes.CreateAgentSession,
        payloadRef,
        correlationId: session.Id,
        idempotencyKey: session.Id,
        runnerId: agent.ControllerId,
        agentId: agent.Id,
        sessionId: session.Id);
    var enqueue = await runnerStore.EnqueueCommandAsync(command, cancellationToken);
    if (enqueue.Status == ControllerCommandEnqueueStatus.Conflict)
    {
        return Results.Conflict(new { message = enqueue.Message });
    }

    command = enqueue.Command!;
    await PublishAsync(sessionStore, hub, session.Id, null, "agent_session.command_queued", command, cancellationToken);
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
    var command = ControllerCommandFactory.Create(
        ControllerCommandTypes.StartInvocation,
        payloadRef,
        correlationId: turn.Id,
        idempotencyKey: turn.Id,
        runnerId: session.ControllerId,
        agentId: session.AgentId,
        sessionId: session.Id);
    var enqueue = await runnerStore.EnqueueCommandAsync(command, cancellationToken);
    if (enqueue.Status == ControllerCommandEnqueueStatus.Conflict)
    {
        return Results.Conflict(new { message = enqueue.Message });
    }

    command = enqueue.Command!;
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

app.MapPost("/api/agent-sessions/{sessionId}/history/sync", async (
    string sessionId,
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
    if (string.IsNullOrWhiteSpace(session.AgentId) || string.IsNullOrWhiteSpace(session.ControllerId))
    {
        return Results.Conflict(new { message = "Session is not owned by an active agent controller." });
    }

    var commandId = Ids.New("cmd");
    var command = ControllerCommandFactory.Create(
        ControllerCommandTypes.SynchronizeSessionHistory,
        payloadRef: null,
        correlationId: sessionId,
        idempotencyKey: $"history:{sessionId}:{commandId}",
        runnerId: session.ControllerId,
        agentId: session.AgentId,
        sessionId: sessionId,
        commandId: commandId);
    var enqueue = await runnerStore.EnqueueCommandAsync(command, cancellationToken);
    if (enqueue.Status == ControllerCommandEnqueueStatus.Conflict)
    {
        return Results.Conflict(new { message = enqueue.Message });
    }

    command = enqueue.Command!;
    await PublishAsync(
        store,
        hub,
        sessionId,
        null,
        "agent_session.history_sync_queued",
        command,
        cancellationToken);
    return Results.Accepted(
        $"/api/v1/controllers/{session.ControllerId}/commands/{command.CommandId}",
        command);
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
    var command = ControllerCommandFactory.Create(
        ControllerCommandTypes.CloseAgentSession,
        payloadRef,
        correlationId: sessionId,
        idempotencyKey: $"cancel:{sessionId}",
        runnerId: session.ControllerId,
        agentId: session.AgentId,
        sessionId: session.Id);
    var enqueue = await runnerStore.EnqueueCommandAsync(command, cancellationToken);
    if (enqueue.Status == ControllerCommandEnqueueStatus.Conflict)
    {
        return Results.Conflict(new { message = enqueue.Message });
    }

    command = enqueue.Command!;
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

static async Task ApplyControllerEventAsync(
    ControllerEvent controllerEvent,
    RunnerControlStore runnerStore,
    AgentStore agentStore,
    AgentSessionStore sessionStore,
    CancellationToken cancellationToken)
{
    switch (controllerEvent.EventType)
    {
        case ControllerEventTypes.AgentRuntimeReady:
        case ControllerEventTypes.AgentRuntimeLost:
        case ControllerEventTypes.AgentRuntimeStopped:
        {
            if (controllerEvent.Target.RuntimeId is not { } runtimeId)
            {
                break;
            }
            var agent = await agentStore.GetAgentAsync(runtimeId, cancellationToken);
            if (agent is not null)
            {
                var status = controllerEvent.EventType switch
                {
                    ControllerEventTypes.AgentRuntimeReady => "ready",
                    ControllerEventTypes.AgentRuntimeStopped => "stopped",
                    _ => "failed"
                };
                await agentStore.UpsertAgentAsync(
                    agent with
                    {
                        Status = status,
                        ReadyAt = status == "ready" ? controllerEvent.OccurredAt : agent.ReadyAt,
                        EndedAt = status is "stopped" or "failed"
                            ? controllerEvent.OccurredAt
                            : agent.EndedAt
                    },
                    cancellationToken);
            }
            break;
        }

        case ControllerEventTypes.AgentSessionCreated:
        {
            var sessionId = controllerEvent.Target.AgentSessionId
                            ?? controllerEvent.Aggregate.Id;
            var session = await sessionStore.GetSessionAsync(sessionId, cancellationToken);
            if (session is not null)
            {
                await sessionStore.UpsertSessionAsync(
                    session with
                    {
                        Status = "ready",
                        ReadyAt = controllerEvent.OccurredAt,
                        FailedAt = null,
                        FailureSummary = null
                    },
                    cancellationToken);
            }
            break;
        }

        case ControllerEventTypes.AgentSessionClosed:
        {
            var sessionId = controllerEvent.Target.AgentSessionId
                            ?? controllerEvent.Aggregate.Id;
            var session = await sessionStore.GetSessionAsync(sessionId, cancellationToken);
            if (session is not null)
            {
                var cancelled = session.Status == "cancelling";
                await sessionStore.UpsertSessionAsync(
                    session with
                    {
                        Status = cancelled ? "cancelled" : "failed",
                        EndedAt = controllerEvent.OccurredAt,
                        FailedAt = cancelled ? null : controllerEvent.OccurredAt,
                        FailureSummary = cancelled
                            ? null
                            : await PayloadSummaryAsync(
                                runnerStore,
                                controllerEvent.PayloadRef,
                                "Agent session failed.",
                                cancellationToken)
                    },
                    cancellationToken);
            }
            break;
        }

        case ControllerEventTypes.AgentSessionHistorySynchronized:
        {
            var history = await PayloadJsonAsync<AgentChatHistory>(
                runnerStore,
                controllerEvent.PayloadRef,
                cancellationToken);
            if (history is not null)
            {
                await sessionStore.ReconcileHistoryAsync(history, cancellationToken);
            }
            break;
        }

        case ControllerEventTypes.AgentSessionHistorySynchronizationFailed:
            break;

        case ControllerEventTypes.OutcomeReported:
        {
            var invocationId = ControllerEventInvocationId(controllerEvent);
            var sessionId = controllerEvent.Target.AgentSessionId;
            if (sessionId is null || invocationId is null)
            {
                break;
            }

            var turn = await sessionStore.GetTurnAsync(
                sessionId,
                invocationId,
                cancellationToken);
            if (turn is null)
            {
                break;
            }

            var result = controllerEvent.PayloadRef?.ContentType.StartsWith(
                "application/vnd.tradecraft.agent-turn-result+json",
                StringComparison.OrdinalIgnoreCase) == true
                    ? await PayloadJsonAsync<AgentTurnResult>(
                        runnerStore,
                        controllerEvent.PayloadRef,
                        cancellationToken)
                    : null;
            var failed = result is null
                         || !result.Status.Equals(
                             "completed",
                             StringComparison.OrdinalIgnoreCase);
            await sessionStore.UpsertTurnAsync(
                turn with
                {
                    Status = result?.Status ?? "failed",
                    CompletedAt = controllerEvent.OccurredAt,
                    Response = result?.Message,
                    FailureSummary = result?.FailureReport?.Summary
                        ?? (failed
                            ? await PayloadSummaryAsync(
                                runnerStore,
                                controllerEvent.PayloadRef,
                                "Agent turn failed.",
                                cancellationToken)
                            : null),
                    FailureDetail = result?.FailureReport?.Detail,
                    Diagnostics = result?.Diagnostics
                },
                cancellationToken);
            break;
        }

    }
}

static async Task<T?> PayloadJsonAsync<T>(
    RunnerControlStore runnerStore,
    ContentReference? payloadRef,
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
    ContentReference? payloadRef,
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

static string ContentId(ContentReference contentRef)
{
    return contentRef.Uri.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
}

static string? ControllerEventInvocationId(ControllerEvent controllerEvent)
{
    return controllerEvent.Target.InvocationId
           ?? controllerEvent.Correlation.InvocationId;
}

static string UiEventType(ControllerEvent controllerEvent)
{
    return controllerEvent.EventType switch
    {
        ControllerEventTypes.AgentSessionHistorySynchronized =>
            "agent_session.history_synced",
        ControllerEventTypes.AgentSessionHistorySynchronizationFailed =>
            "agent_session.history_sync_failed",
        ControllerEventTypes.OutcomeReported =>
            "agent_turn.completed",
        ControllerEventTypes.AgentRuntimeReady =>
            "agent.ready",
        ControllerEventTypes.AgentRuntimeStopped =>
            "agent.stopped",
        ControllerEventTypes.AgentRuntimeLost =>
            "agent.failed",
        ControllerEventTypes.AgentSessionCreated =>
            "agent_session.ready",
        ControllerEventTypes.AgentSessionClosed =>
            "agent_session.closed",
        _ => controllerEvent.EventType
    };
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

    public async Task<bool> UpsertInventoryAgentAsync(
        string runnerId,
        ControllerInventoryObservation inventory,
        CancellationToken cancellationToken)
    {
        var agentId = inventory.AgentId ?? inventory.AgentSessionId;
        var observed = AgentRecord.FromInventory(runnerId, inventory);
        if (_agents.TryGetValue(agentId, out var existing))
        {
            if (!existing.ControllerId.Equals(runnerId, StringComparison.Ordinal))
            {
                return false;
            }
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
        return true;
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
        ControllerSessionObservation inventory,
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
                Status = IsTerminalSessionStatus(existing.Status) ? existing.Status : status,
                EndedAt = existing.EndedAt ?? observed.EndedAt,
                FailedAt = existing.FailedAt ?? observed.FailedAt,
                FailureSummary = existing.FailureSummary
            };
        }
        await UpsertSessionAsync(observed, cancellationToken);
    }

    public async Task UpsertInventorySessionAsync(
        string runnerId,
        ControllerInventoryObservation agent,
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

    public async Task ReconcileHistoryAsync(AgentChatHistory history, CancellationToken cancellationToken)
    {
        var existing = _turns.Values
            .Where(turn => turn.SessionId == history.AgentSessionId)
            .OrderBy(turn => turn.CreatedAt)
            .ToList();
        var matchedTurnIds = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < history.Messages.Count;)
        {
            var userMessage = history.Messages[index];
            if (!userMessage.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            var assistantMessages = new List<AgentChatMessage>();
            index++;
            while (index < history.Messages.Count
                && !history.Messages[index].Role.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                if (history.Messages[index].Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
                {
                    assistantMessages.Add(history.Messages[index]);
                }
                index++;
            }

            var turn = existing.FirstOrDefault(candidate =>
                !matchedTurnIds.Contains(candidate.Id)
                && candidate.SourceUserMessageId == userMessage.SourceMessageId)
                ?? existing.FirstOrDefault(candidate =>
                    !matchedTurnIds.Contains(candidate.Id)
                    && candidate.SourceUserMessageId is null
                    && candidate.Prompt == userMessage.Text);
            turn ??= AgentTurnRecord.FromHistory(history.AgentSessionId, userMessage);
            matchedTurnIds.Add(turn.Id);

            var response = assistantMessages.Count == 0
                ? turn.Response
                : string.Join(
                    "\n\n",
                    assistantMessages
                        .Select(message => message.Text)
                        .Where(text => !string.IsNullOrWhiteSpace(text)));
            var completedAt = assistantMessages.Count == 0
                ? turn.CompletedAt
                : assistantMessages.Max(message => message.CompletedAt ?? message.CreatedAt);
            var reconciled = turn with
            {
                Prompt = userMessage.Text,
                Status = assistantMessages.Count > 0 ? "completed" : turn.Status,
                CreatedAt = userMessage.CreatedAt,
                CompletedAt = completedAt,
                Response = response,
                SourceUserMessageId = userMessage.SourceMessageId,
                SourceAssistantMessageIds = assistantMessages.Select(message => message.SourceMessageId).ToArray(),
                HistorySyncedAt = history.ObservedAt
            };
            await UpsertTurnAsync(reconciled, cancellationToken);
        }
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
    private static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _commandMutationLock = new(1, 1);
    private readonly ConcurrentDictionary<string, ControllerCommand> _commands = new();
    private readonly ConcurrentDictionary<string, ControllerCommandIdempotencyRecord>
        _idempotencyRecords = new();
    private readonly ConcurrentDictionary<string, ControllerCommandAcknowledgement>
        _acknowledgements = new();
    private readonly ConcurrentDictionary<string, ControllerCommandLeaseRenewal>
        _leaseRenewals = new();
    private readonly ConcurrentDictionary<string, ControllerCommandCompletion> _completions = new();
    private readonly ConcurrentBag<ControllerEvent> _events = [];
    private readonly ConcurrentDictionary<string, StoredClaimCheckContent> _content = new();
    private readonly ConcurrentDictionary<string, ControllerHeartbeat> _controllerHeartbeats = new();
    private readonly string _runtimeRoot;
    private readonly TimeProvider _timeProvider;

    public RunnerControlStore() : this(RuntimePaths.Root, TimeProvider.System)
    {
    }

    public RunnerControlStore(TimeProvider timeProvider) : this(RuntimePaths.Root, timeProvider)
    {
    }

    public RunnerControlStore(string runtimeRoot) : this(runtimeRoot, TimeProvider.System)
    {
    }

    public RunnerControlStore(string runtimeRoot, TimeProvider timeProvider)
    {
        _runtimeRoot = runtimeRoot;
        _timeProvider = timeProvider;
        LoadCommands();
        LoadIdempotencyRecords();
        LoadAcknowledgements();
        LoadLeaseRenewals();
        LoadCompletions();
        LoadEvents();
        LoadContentMetadata();
        LoadControllerHeartbeats();
    }

    public async Task<ControllerCommandEnqueueResult> EnqueueCommandAsync(
        ControllerCommand command,
        CancellationToken cancellationToken)
    {
        var scope = ControllerCommandIdempotency.Scope(command);
        var fingerprint = ControllerCommandIdempotency.Fingerprint(command);
        await _commandMutationLock.WaitAsync(cancellationToken);
        try
        {
            if (_idempotencyRecords.TryGetValue(scope, out var existingRecord))
            {
                if (!existingRecord.Fingerprint.Equals(
                        fingerprint,
                        StringComparison.Ordinal))
                {
                    return ControllerCommandEnqueueResult.Conflict(
                        $"Idempotency key '{command.IdempotencyKey}' was already used " +
                        $"for a different {command.CommandType} command in controller " +
                        $"scope '{command.Target.ControllerId ?? "*"}'.");
                }

                if (!_commands.TryGetValue(
                        existingRecord.Command.CommandId,
                        out var existing))
                {
                    existing = existingRecord.Command;
                    _commands[existing.CommandId] = existing;
                }

                if (!File.Exists(CommandPath(existing.CommandId)))
                {
                    await WriteJsonAsync(
                        CommandPath(existing.CommandId),
                        existing,
                        cancellationToken);
                }

                _completions.TryGetValue(existing.CommandId, out var completion);
                return ControllerCommandEnqueueResult.Replayed(existing, completion);
            }

            var record = new ControllerCommandIdempotencyRecord(
                scope,
                fingerprint,
                command,
                _timeProvider.GetUtcNow());
            await WriteJsonAsync(
                IdempotencyPath(scope),
                record,
                cancellationToken);
            await WriteJsonAsync(
                CommandPath(command.CommandId),
                command,
                cancellationToken);
            _commands[command.CommandId] = command;
            _idempotencyRecords[scope] = record;
            return ControllerCommandEnqueueResult.Accepted(command);
        }
        finally
        {
            _commandMutationLock.Release();
        }
    }

    public Task<IReadOnlyCollection<ControllerCommand>> GetAvailableCommandsAsync(
        string controllerId,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var commands = _commands.Values
            .Where(command => !_completions.ContainsKey(command.CommandId))
            .Where(command => command.AvailableAt is null || command.AvailableAt <= now)
            .Where(command => command.Target.ControllerId is null
                              || command.Target.ControllerId == controllerId)
            .Where(command => command.Execution.Lease is null
                              || command.Execution.Lease.ExpiresAt <= now)
            .OrderBy(command => command.IssuedAt)
            .ToArray();
        return Task.FromResult<IReadOnlyCollection<ControllerCommand>>(commands);
    }

    public async Task<ControllerCommandMutationResult> AcknowledgeCommandAsync(
        ControllerCommandAcknowledgement acknowledgement,
        CancellationToken cancellationToken)
    {
        await _commandMutationLock.WaitAsync(cancellationToken);
        try
        {
            if (!_commands.TryGetValue(acknowledgement.CommandId, out var command))
            {
                return ControllerCommandMutationResult.NotFound();
            }

            var now = _timeProvider.GetUtcNow();
            var currentLease = command.Execution.Lease;
            if (_acknowledgements.TryGetValue(
                    acknowledgement.CommandId,
                    out var existingAcknowledgement)
                && (currentLease is null || currentLease.ExpiresAt > now))
            {
                return AcknowledgementsEquivalent(
                    existingAcknowledgement,
                    acknowledgement)
                    ? ControllerCommandMutationResult.Ok(
                        command,
                        acknowledgement: existingAcknowledgement)
                    : ControllerCommandMutationResult.Conflict(
                        "Command acknowledgement conflicts with the first accepted " +
                        "acknowledgement.");
            }

            if (_completions.ContainsKey(command.CommandId))
            {
                return ControllerCommandMutationResult.Conflict(
                    "Command is already complete.");
            }

            if (acknowledgement.Status != CommandAcknowledgementStatuses.Accepted)
            {
                return ControllerCommandMutationResult.Conflict(
                    "The POC command loop must accept a command before execution.");
            }

            if (currentLease is not null
                && currentLease.ExpiresAt > now
                && currentLease.ControllerId != acknowledgement.ControllerId)
            {
                return ControllerCommandMutationResult.Conflict(
                    "Command is leased by another controller.");
            }

            var lease = new CommandLease(
                LeaseId: Ids.New("lease"),
                ControllerId: acknowledgement.ControllerId,
                AcquiredAt: now,
                ExpiresAt: Min(now + DefaultLeaseDuration, command.Execution.Deadline),
                Attempt: (currentLease?.Attempt ?? 0) + 1,
                FencingToken: (currentLease?.FencingToken ?? 0) + 1);
            var claimed = command with
            {
                Target = command.Target with
                {
                    ControllerId = acknowledgement.ControllerId
                },
                Execution = command.Execution with { Lease = lease }
            };
            var accepted = acknowledgement with
            {
                Correlation = command.Correlation,
                Lease = lease
            };
            await WriteJsonAsync(
                CommandPath(command.CommandId),
                claimed,
                cancellationToken);
            await WriteJsonAsync(
                AcknowledgementPath(command.CommandId),
                accepted,
                cancellationToken);
            _commands[command.CommandId] = claimed;
            _acknowledgements[command.CommandId] = accepted;
            return ControllerCommandMutationResult.Ok(
                claimed,
                acknowledgement: accepted);
        }
        finally
        {
            _commandMutationLock.Release();
        }
    }

    public async Task<ControllerCommandMutationResult> RenewCommandLeaseAsync(
        ControllerCommandLeaseRenewal renewal,
        CancellationToken cancellationToken)
    {
        await _commandMutationLock.WaitAsync(cancellationToken);
        try
        {
            if (!_commands.TryGetValue(renewal.CommandId, out var command))
            {
                return ControllerCommandMutationResult.NotFound();
            }

            if (_leaseRenewals.TryGetValue(renewal.RenewalId, out var existingRenewal))
            {
                return LeaseRenewalsEquivalent(existingRenewal, renewal)
                    ? ControllerCommandMutationResult.Ok(
                        command,
                        renewal: existingRenewal)
                    : ControllerCommandMutationResult.Conflict(
                        "Command lease renewal conflicts with the first renewal request.");
            }

            if (_completions.ContainsKey(command.CommandId))
            {
                return ControllerCommandMutationResult.Conflict(
                    "Command is already complete.");
            }

            var lease = command.Execution.Lease;
            if (lease is null)
            {
                return ControllerCommandMutationResult.Conflict(
                    "Command cannot renew a missing lease.");
            }

            if (!lease.ControllerId.Equals(renewal.ControllerId, StringComparison.Ordinal)
                || !lease.LeaseId.Equals(renewal.LeaseId, StringComparison.Ordinal)
                || lease.FencingToken != renewal.FencingToken)
            {
                return ControllerCommandMutationResult.Conflict(
                    "Command lease does not match renewal.");
            }

            var now = _timeProvider.GetUtcNow();
            if (lease.ExpiresAt <= now)
            {
                return ControllerCommandMutationResult.Conflict(
                    "Command lease is expired and must be claimed again.");
            }

            if (renewal.RequestedExpiresAt <= lease.ExpiresAt)
            {
                return ControllerCommandMutationResult.Conflict(
                    "Command lease renewal must extend the current lease.");
            }

            var renewedLease = lease with
            {
                ExpiresAt = Min(
                    Min(renewal.RequestedExpiresAt, now + DefaultLeaseDuration),
                    command.Execution.Deadline)
            };
            var renewedCommand = command with
            {
                Execution = command.Execution with { Lease = renewedLease }
            };
            var acceptedRenewal = renewal with { Correlation = command.Correlation };
            await WriteJsonAsync(
                CommandPath(command.CommandId),
                renewedCommand,
                cancellationToken);
            await WriteJsonAsync(
                LeaseRenewalPath(renewal.RenewalId),
                acceptedRenewal,
                cancellationToken);
            _commands[command.CommandId] = renewedCommand;
            _leaseRenewals[renewal.RenewalId] = acceptedRenewal;
            return ControllerCommandMutationResult.Ok(
                renewedCommand,
                renewal: acceptedRenewal);
        }
        finally
        {
            _commandMutationLock.Release();
        }
    }

    public async Task<ControllerCommandMutationResult> CompleteCommandAsync(
        ControllerCommandCompletion completion,
        CancellationToken cancellationToken)
    {
        await _commandMutationLock.WaitAsync(cancellationToken);
        try
        {
            if (!_commands.TryGetValue(completion.CommandId, out var command))
            {
                return ControllerCommandMutationResult.NotFound();
            }

            if (_completions.TryGetValue(
                    completion.CommandId,
                    out var existingCompletion))
            {
                return CompletionsEquivalent(existingCompletion, completion)
                    ? ControllerCommandMutationResult.Ok(
                        command,
                        completion: existingCompletion)
                    : ControllerCommandMutationResult.Conflict(
                        "Command completion conflicts with the first terminal result.");
            }

            var lease = command.Execution.Lease;
            if (lease is null
                || lease.ControllerId != completion.ControllerId
                || lease.FencingToken != completion.FencingToken)
            {
                return ControllerCommandMutationResult.Conflict(
                    "Command lease does not match completion.");
            }

            await WriteJsonAsync(
                CompletionPath(completion.CommandId),
                completion,
                cancellationToken);
            _completions[completion.CommandId] = completion;
            return ControllerCommandMutationResult.Ok(
                command,
                completion: completion);
        }
        finally
        {
            _commandMutationLock.Release();
        }
    }

    public async Task<ControllerEventMutationResult> AddEventAsync(
        ControllerEvent controllerEvent,
        CancellationToken cancellationToken)
    {
        if (controllerEvent.Correlation.CommandId is not null)
        {
            if (!_commands.TryGetValue(controllerEvent.Correlation.CommandId, out var command))
            {
                return ControllerEventMutationResult.NotFound();
            }

            var lease = command.Execution.Lease;
            if (lease is null
                || lease.ControllerId != controllerEvent.ControllerId
                || controllerEvent.FencingToken is null
                || lease.FencingToken != controllerEvent.FencingToken)
            {
                return ControllerEventMutationResult.Conflict(
                    "Command lease does not match controller event.");
            }
        }

        _events.Add(controllerEvent);
        var path = ControllerEventsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.AppendAllTextAsync(
            path,
            JsonSerializer.Serialize(controllerEvent, JsonLineOptions) + Environment.NewLine,
            cancellationToken);
        return ControllerEventMutationResult.Ok();
    }

    public Task<IReadOnlyCollection<ControllerEvent>> GetEventsAsync(
        CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyCollection<ControllerEvent>>(
            _events.OrderBy(e => e.OccurredAt).ToArray());
    }

    public async Task<ControllerContentUploadResponse> SaveContentAsync(
        ControllerContentUploadRequest request,
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
        var contentRef = new ContentReference(
            Uri: $"tradecraft://content/{contentId}",
            Sha256: actualSha,
            ContentType: request.ContentType,
            Length: bytes.LongLength);
        _content[contentId] = new StoredClaimCheckContent(contentRef, bytes);
        Directory.CreateDirectory(ContentRoot());
        await File.WriteAllBytesAsync(ContentBlobPath(contentId), bytes, cancellationToken);
        await WriteJsonAsync(ContentMetadataPath(contentId), contentRef, cancellationToken);
        return new ControllerContentUploadResponse(contentRef);
    }

    public async Task<ContentReference> SaveJsonContentAsync<T>(
        T value,
        string contentType,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            value,
            ControllerProtocolJson.Options);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var upload = new ControllerContentUploadRequest(
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

        var contentRef = await ReadJsonAsync<ContentReference>(
            metadataPath,
            cancellationToken);
        var bytes = await File.ReadAllBytesAsync(blobPath, cancellationToken);
        content = new StoredClaimCheckContent(contentRef, bytes);
        _content[contentId] = content;
        return content;
    }

    public Task UpsertControllerHeartbeatAsync(
        ControllerHeartbeat heartbeat,
        CancellationToken cancellationToken)
    {
        _controllerHeartbeats[heartbeat.ControllerId] = heartbeat;
        return WriteJsonAsync(
            ControllerHeartbeatPath(heartbeat.ControllerId),
            heartbeat,
            cancellationToken);
    }

    public Task<ControllerHeartbeat?> GetControllerHeartbeatAsync(
        string controllerId,
        CancellationToken cancellationToken)
    {
        _controllerHeartbeats.TryGetValue(controllerId, out var heartbeat);
        return Task.FromResult(heartbeat);
    }

    public Task<IReadOnlyCollection<ControllerHeartbeat>> GetControllerHeartbeatsAsync(
        CancellationToken cancellationToken)
    {
        var heartbeats = _controllerHeartbeats.Values
            .OrderBy(heartbeat => heartbeat.ControllerId, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult<IReadOnlyCollection<ControllerHeartbeat>>(heartbeats);
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
            var command = JsonSerializer.Deserialize<ControllerCommand>(
                File.ReadAllText(path),
                ControllerProtocolJson.Options);
            if (command is not null)
            {
                _commands[command.CommandId] = command;
            }
        }
    }

    private void LoadIdempotencyRecords()
    {
        var root = IdempotencyRoot();
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*.json"))
        {
            var record = JsonSerializer.Deserialize<ControllerCommandIdempotencyRecord>(
                File.ReadAllText(path),
                ControllerProtocolJson.Options);
            if (record is not null)
            {
                _idempotencyRecords[record.Scope] = record;
                _commands.TryAdd(record.Command.CommandId, record.Command);
            }
        }
    }

    private void LoadAcknowledgements()
    {
        var root = AcknowledgementRoot();
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*.json"))
        {
            var acknowledgement =
                JsonSerializer.Deserialize<ControllerCommandAcknowledgement>(
                    File.ReadAllText(path),
                    ControllerProtocolJson.Options);
            if (acknowledgement is not null)
            {
                _acknowledgements[acknowledgement.CommandId] = acknowledgement;
            }
        }
    }

    private void LoadLeaseRenewals()
    {
        var root = LeaseRenewalRoot();
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*.json"))
        {
            var renewal =
                JsonSerializer.Deserialize<ControllerCommandLeaseRenewal>(
                    File.ReadAllText(path),
                    ControllerProtocolJson.Options);
            if (renewal is not null)
            {
                _leaseRenewals[renewal.RenewalId] = renewal;
            }
        }
    }

    private void LoadCompletions()
    {
        var root = CompletionRoot();
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*.json"))
        {
            var completion = JsonSerializer.Deserialize<ControllerCommandCompletion>(
                File.ReadAllText(path),
                ControllerProtocolJson.Options);
            if (completion is not null)
            {
                _completions[completion.CommandId] = completion;
            }
        }
    }

    private void LoadEvents()
    {
        var path = ControllerEventsPath();
        if (!File.Exists(path))
        {
            return;
        }

        foreach (var line in File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            var controllerEvent = JsonSerializer.Deserialize<ControllerEvent>(
                line,
                ControllerProtocolJson.Options);
            if (controllerEvent is not null)
            {
                _events.Add(controllerEvent);
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
            var contentRef = JsonSerializer.Deserialize<ContentReference>(
                File.ReadAllText(path),
                ControllerProtocolJson.Options);
            if (contentRef is not null && File.Exists(blobPath))
            {
                _content[contentId] = new StoredClaimCheckContent(contentRef, File.ReadAllBytes(blobPath));
            }
        }
    }

    private void LoadControllerHeartbeats()
    {
        var root = ControllerHeartbeatRoot();
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*.json"))
        {
            var heartbeat = JsonSerializer.Deserialize<ControllerHeartbeat>(
                File.ReadAllText(path),
                ControllerProtocolJson.Options);
            if (heartbeat is not null)
            {
                _controllerHeartbeats[heartbeat.ControllerId] = heartbeat;
            }
        }
    }

    private string ControllerRoot() => Path.Combine(_runtimeRoot, "controller-v1");
    private string CommandRoot() => Path.Combine(ControllerRoot(), "commands");
    private string CommandPath(string commandId) => Path.Combine(CommandRoot(), $"{commandId}.json");
    private string IdempotencyRoot() => Path.Combine(ControllerRoot(), "idempotency");
    private string IdempotencyPath(string scope) =>
        Path.Combine(
            IdempotencyRoot(),
            $"{ControllerCommandIdempotency.PathToken(scope)}.json");
    private string AcknowledgementRoot() =>
        Path.Combine(ControllerRoot(), "acknowledgements");
    private string AcknowledgementPath(string commandId) =>
        Path.Combine(AcknowledgementRoot(), $"{commandId}.json");
    private string LeaseRenewalRoot() =>
        Path.Combine(ControllerRoot(), "lease-renewals");
    private string LeaseRenewalPath(string renewalId) =>
        Path.Combine(LeaseRenewalRoot(), $"{renewalId}.json");
    private string CompletionRoot() => Path.Combine(ControllerRoot(), "completions");
    private string CompletionPath(string commandId) => Path.Combine(CompletionRoot(), $"{commandId}.json");
    private string ControllerEventsPath() => Path.Combine(ControllerRoot(), "events.jsonl");
    private string ControllerHeartbeatRoot() => Path.Combine(ControllerRoot(), "controllers");
    private string ControllerHeartbeatPath(string controllerId) =>
        Path.Combine(ControllerHeartbeatRoot(), $"{controllerId}.json");
    private string ContentRoot() => Path.Combine(ControllerRoot(), "content");
    private string ContentBlobPath(string contentId) => Path.Combine(ContentRoot(), $"{contentId}.bin");
    private string ContentMetadataPath(string contentId) => Path.Combine(ContentRoot(), $"{contentId}.json");

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(value, ControllerProtocolJson.Options),
            cancellationToken);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<T>(json, ControllerProtocolJson.Options)
            ?? throw new InvalidDataException($"{path} did not contain a valid {typeof(T).Name}.");
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left : right;

    private static bool AcknowledgementsEquivalent(
        ControllerCommandAcknowledgement left,
        ControllerCommandAcknowledgement right)
    {
        return ControllerCommandIdempotency.SemanticHash(new
        {
            left.MessageType,
            left.ProtocolVersion,
            left.SchemaVersion,
            left.CommandId,
            left.ControllerId,
            left.Status,
            Error = NormalizeError(left.Error),
            left.Extensions
        }) == ControllerCommandIdempotency.SemanticHash(new
        {
            right.MessageType,
            right.ProtocolVersion,
            right.SchemaVersion,
            right.CommandId,
            right.ControllerId,
            right.Status,
            Error = NormalizeError(right.Error),
            right.Extensions
        });
    }

    private static bool CompletionsEquivalent(
        ControllerCommandCompletion left,
        ControllerCommandCompletion right)
    {
        return ControllerCommandIdempotency.SemanticHash(new
        {
            left.MessageType,
            left.ProtocolVersion,
            left.SchemaVersion,
            left.CommandId,
            left.ControllerId,
            left.DeliveryStatus,
            left.FencingToken,
            Result = NormalizeContentReference(left.ResultRef),
            Error = NormalizeError(left.Error),
            left.Extensions
        }) == ControllerCommandIdempotency.SemanticHash(new
        {
            right.MessageType,
            right.ProtocolVersion,
            right.SchemaVersion,
            right.CommandId,
            right.ControllerId,
            right.DeliveryStatus,
            right.FencingToken,
            Result = NormalizeContentReference(right.ResultRef),
            Error = NormalizeError(right.Error),
            right.Extensions
        });
    }

    private static bool LeaseRenewalsEquivalent(
        ControllerCommandLeaseRenewal left,
        ControllerCommandLeaseRenewal right)
    {
        return ControllerCommandIdempotency.SemanticHash(new
        {
            left.MessageType,
            left.ProtocolVersion,
            left.SchemaVersion,
            left.RenewalId,
            left.CommandId,
            left.ControllerId,
            left.LeaseId,
            left.FencingToken,
            left.RequestedExpiresAt,
            left.Extensions
        }) == ControllerCommandIdempotency.SemanticHash(new
        {
            right.MessageType,
            right.ProtocolVersion,
            right.SchemaVersion,
            right.RenewalId,
            right.CommandId,
            right.ControllerId,
            right.LeaseId,
            right.FencingToken,
            right.RequestedExpiresAt,
            right.Extensions
        });
    }

    private static object? NormalizeContentReference(ContentReference? contentRef)
    {
        return contentRef is null
            ? null
            : new
            {
                contentRef.Sha256,
                contentRef.ContentType,
                contentRef.Length,
                contentRef.SchemaRef,
                contentRef.Encryption
            };
    }

    private static object? NormalizeError(ProtocolError? error)
    {
        return error is null
            ? null
            : new
            {
                error.Code,
                error.Classification,
                error.Summary,
                error.Retryable,
                error.TargetUsable,
                error.ReconciliationRequired,
                Diagnostic = NormalizeContentReference(error.DiagnosticRef),
                error.ProviderRequestId
            };
    }

    private static JsonSerializerOptions JsonLineOptions { get; } = new(ControllerProtocolJson.Options)
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

public enum ControllerCommandMutationStatus
{
    Ok,
    NotFound,
    Conflict
}

public enum ControllerEventMutationStatus
{
    Ok,
    NotFound,
    Conflict
}

public sealed record ControllerEventMutationResult(
    ControllerEventMutationStatus Status,
    string? Message)
{
    public static ControllerEventMutationResult Ok() =>
        new(ControllerEventMutationStatus.Ok, null);

    public static ControllerEventMutationResult NotFound() =>
        new(ControllerEventMutationStatus.NotFound, null);

    public static ControllerEventMutationResult Conflict(string message) =>
        new(ControllerEventMutationStatus.Conflict, message);
}

public sealed record ControllerCommandMutationResult(
    ControllerCommandMutationStatus Status,
    ControllerCommand? Command,
    ControllerCommandAcknowledgement? Acknowledgement,
    ControllerCommandLeaseRenewal? Renewal,
    ControllerCommandCompletion? Completion,
    string? Message)
{
    public static ControllerCommandMutationResult Ok(
        ControllerCommand command,
        ControllerCommandAcknowledgement? acknowledgement = null,
        ControllerCommandLeaseRenewal? renewal = null,
        ControllerCommandCompletion? completion = null) =>
        new(
            ControllerCommandMutationStatus.Ok,
            command,
            acknowledgement,
            renewal,
            completion,
            null);

    public static ControllerCommandMutationResult NotFound() =>
        new(ControllerCommandMutationStatus.NotFound, null, null, null, null, null);

    public static ControllerCommandMutationResult Conflict(string message) =>
        new(ControllerCommandMutationStatus.Conflict, null, null, null, null, message);
}

public readonly record struct StoredClaimCheckContent(ContentReference ContentRef, byte[] Bytes)
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

public static class ControllerCommandFactory
{
    public static ControllerCommand Create(
        string type,
        ContentReference? payloadRef,
        string correlationId,
        string idempotencyKey,
        string? runnerId = null,
        string? agentId = null,
        string? sessionId = null,
        string? commandId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var resolvedCommandId = commandId ?? Ids.New("cmd");
        return new ControllerCommand(
            MessageType: ControllerMessageTypes.Command,
            ProtocolVersion: ControllerProtocolVersions.Protocol,
            SchemaVersion: ControllerProtocolVersions.Schema,
            CommandId: resolvedCommandId,
            CommandType: type,
            IdempotencyKey: idempotencyKey,
            IssuedAt: now,
            Target: new ResourceTarget(
                ControllerId: runnerId,
                RuntimeId: agentId,
                AgentSessionId: sessionId,
                InvocationId: type == ControllerCommandTypes.StartInvocation
                    ? correlationId
                    : null),
            Correlation: new ProtocolCorrelation(
                CommandId: resolvedCommandId,
                CorrelationId: correlationId,
                InvocationId: type == ControllerCommandTypes.StartInvocation
                    ? correlationId
                    : null),
            AuthorizationContext: new AuthorizationContext(
                SubjectRef: "system://tradecraft-orchestrator",
                GrantRef: $"authorization-grant://poc/{correlationId}",
                IssuedAt: now,
                ExpiresAt: now.AddHours(1)),
            Execution: new CommandExecution(
                Deadline: now.AddHours(1),
                HeartbeatInterval: "PT30S"),
            AvailableAt: now,
            PayloadRef: payloadRef);
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

public sealed record ControllerInventoryObservation(
    string AgentSessionId,
    string Status,
    string RuntimePath,
    string WorkspacePath,
    string? OpenCodeEndpoint,
    int? OpenCodePid,
    DateTimeOffset ObservedAt,
    string? AgentId,
    IReadOnlyList<ControllerSessionObservation> Sessions)
{
    public static IReadOnlyList<ControllerInventoryObservation> FromHeartbeat(
        ControllerHeartbeat heartbeat)
    {
        var sessions = heartbeat.Inventory.Sessions ?? [];
        return heartbeat.Inventory.Runtimes
            .Select(runtime =>
            {
                var extension = PocExtension(runtime.Extensions);
                return new ControllerInventoryObservation(
                    AgentSessionId: ReadString(extension, "agent_session_id")
                        ?? runtime.RuntimeId,
                    Status: runtime.Status,
                    RuntimePath: ReadString(extension, "runtime_path") ?? string.Empty,
                    WorkspacePath: ReadString(extension, "workspace_path") ?? string.Empty,
                    OpenCodeEndpoint: ReadString(extension, "opencode_endpoint"),
                    OpenCodePid: ReadInt32(extension, "opencode_pid"),
                    ObservedAt: runtime.UpdatedAt,
                    AgentId: runtime.RuntimeId,
                    Sessions: sessions
                        .Where(session => session.RuntimeId == runtime.RuntimeId)
                        .Select(ControllerSessionObservation.FromResource)
                        .ToArray());
            })
            .ToArray();
    }

    private static JsonElement? PocExtension(
        IReadOnlyDictionary<string, JsonElement>? extensions)
    {
        return extensions is not null
               && extensions.TryGetValue("tradecraft.poc", out var extension)
            ? extension
            : null;
    }

    internal static string? ReadString(JsonElement? extension, string propertyName)
    {
        return extension is { ValueKind: JsonValueKind.Object } value
               && value.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static int? ReadInt32(JsonElement? extension, string propertyName)
    {
        return extension is { ValueKind: JsonValueKind.Object } value
               && value.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.Number
            ? property.GetInt32()
            : null;
    }
}

public sealed record ControllerSessionObservation(
    string SessionId,
    string Status,
    string RuntimePath,
    string? OpenCodeSessionId,
    DateTimeOffset ObservedAt)
{
    public static ControllerSessionObservation FromResource(
        AgentSessionResource session)
    {
        JsonElement? extension = session.Extensions is not null
                                 && session.Extensions.TryGetValue(
                                     "tradecraft.poc",
                                     out var value)
            ? value
            : null;
        return new ControllerSessionObservation(
            SessionId: session.AgentSessionId,
            Status: session.Status,
            RuntimePath: ControllerInventoryObservation.ReadString(
                extension,
                "runtime_path") ?? string.Empty,
            OpenCodeSessionId: session.ProviderSessionRef,
            ObservedAt: session.UpdatedAt);
    }
}

public sealed record AgentControllerRecord(
    string RunnerId,
    string Status,
    DateTimeOffset ObservedAt,
    IReadOnlyList<AgentControllerAgentRecord> Agents)
{
    private static readonly TimeSpan ActiveHeartbeatWindow = TimeSpan.FromSeconds(30);

    public static bool IsActive(ControllerHeartbeat heartbeat)
    {
        return heartbeat.ObservedAt >= DateTimeOffset.UtcNow - ActiveHeartbeatWindow;
    }

    public static AgentControllerRecord FromHeartbeat(ControllerHeartbeat heartbeat)
    {
        var agents = ControllerInventoryObservation.FromHeartbeat(heartbeat);
        return new AgentControllerRecord(
            RunnerId: heartbeat.ControllerId,
            Status: heartbeat.Status,
            ObservedAt: heartbeat.ObservedAt,
            Agents: agents.Select(AgentControllerAgentRecord.FromInventory).ToArray());
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
    IReadOnlyList<ControllerSessionObservation>? Sessions = null)
{
    public static AgentControllerAgentRecord FromInventory(ControllerInventoryObservation agent)
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

    public static AgentRecord FromInventory(
        string controllerId,
        ControllerInventoryObservation inventory)
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

    public static AgentSessionRecord FromInventory(
        string controllerId,
        ControllerInventoryObservation agent)
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
    HarnessDiagnostics? Diagnostics,
    string? SourceUserMessageId = null,
    IReadOnlyList<string>? SourceAssistantMessageIds = null,
    DateTimeOffset? HistorySyncedAt = null)
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
            Diagnostics: null,
            SourceAssistantMessageIds: []);
    }

    public static AgentTurnRecord FromHistory(string sessionId, AgentChatMessage userMessage)
    {
        var sourceHash = Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(userMessage.SourceMessageId)))[..24]
            .ToLowerInvariant();
        return new AgentTurnRecord(
            Id: $"turn_history_{sourceHash}",
            SessionId: sessionId,
            Type: "prompt_response",
            Prompt: userMessage.Text,
            Status: "submitted",
            CreatedAt: userMessage.CreatedAt,
            CompletedAt: null,
            Response: null,
            FailureSummary: null,
            FailureDetail: null,
            Diagnostics: null,
            SourceUserMessageId: userMessage.SourceMessageId,
            SourceAssistantMessageIds: []);
    }
}

public sealed record AgentChatHistory(
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("agent_session_id")] string AgentSessionId,
    [property: JsonPropertyName("opencode_session_id")] string OpenCodeSessionId,
    [property: JsonPropertyName("observed_at")] DateTimeOffset ObservedAt,
    [property: JsonPropertyName("messages")] IReadOnlyList<AgentChatMessage> Messages);

public sealed record AgentChatMessage(
    [property: JsonPropertyName("source_message_id")] string SourceMessageId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("completed_at")] DateTimeOffset? CompletedAt);

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
