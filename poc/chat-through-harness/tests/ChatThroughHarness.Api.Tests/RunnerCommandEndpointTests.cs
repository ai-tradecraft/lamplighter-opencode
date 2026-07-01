namespace ChatThroughHarness.Api.Tests;

using System.Collections.Immutable;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ChatThroughHarness.Protocol.V1;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

public sealed class RunnerCommandEndpointTests(
    WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task SystemInfoPublishesPortalContract()
    {
        // Arrange
        using var client = factory.CreateClient();

        // Act
        using var response = await client.GetAsync("/api/system/info");
        response.EnsureSuccessStatusCode();
        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        // Assert
        Assert.Equal(
            "chat-through-harness-api",
            payload.RootElement.GetProperty("service").GetString());
        Assert.Contains(
            payload.RootElement.GetProperty("capabilities").EnumerateArray(),
            capability => capability.GetString() == "multi-session-agents");
    }

    [Fact]
    public async Task ControllerHeartbeat_WhenPostedToV1Route_ThenProjectsInventory()
    {
        // Arrange
        using var client = factory.CreateClient();
        var observedAt = DateTimeOffset.UtcNow;
        var heartbeat = Heartbeat(
            "controller_inventory",
            observedAt,
            [
                Runtime(
                    "controller_inventory",
                    "agent_inventory",
                    observedAt,
                    "/tmp/runtime",
                    "/tmp/workspace")
            ],
            [
                Session(
                    "agent_inventory",
                    "session_inventory",
                    observedAt,
                    "/tmp/runtime/sessions/session_inventory")
            ]);

        // Act
        var response = await client.PostAsJsonAsync(
            "/api/v1/controllers/heartbeats",
            heartbeat,
            ControllerProtocolJson.Options);
        response.EnsureSuccessStatusCode();
        var controllers = await client.GetFromJsonAsync<
            IReadOnlyCollection<AgentControllerRecord>>(
            "/api/agent-controllers",
            JsonDefaults.Options);

        // Assert
        Assert.NotNull(controllers);
        var controller = Assert.Single(
            controllers,
            item => item.RunnerId == "controller_inventory");
        var agent = Assert.Single(controller.Agents);
        Assert.Equal("agent_inventory", agent.AgentId);
        Assert.Equal("/tmp/workspace", agent.WorkspacePath);
        Assert.Single(agent.Sessions!);
        var projectedSession = await client.GetFromJsonAsync<AgentSessionRecord>(
            "/api/agent-sessions/session_inventory",
            JsonDefaults.Options);
        Assert.Equal("ready", Assert.IsType<AgentSessionRecord>(projectedSession).Status);
    }

    [Fact]
    public async Task CreateAgentAndSession_WhenControllerIsActive_ThenQueuesV1Commands()
    {
        // Arrange
        using var client = factory.CreateClient();
        const string controllerId = "controller_create";
        await PostHeartbeatAsync(client, controllerId);

        // Act
        var agentResponse = await client.PostAsJsonAsync(
            $"/api/agent-controllers/{controllerId}/agents",
            new CreateAgentRequest());
        agentResponse.EnsureSuccessStatusCode();
        var agent = Assert.IsType<AgentRecord>(
            await agentResponse.Content.ReadFromJsonAsync<AgentRecord>(
                JsonDefaults.Options));
        var commands = await PollCommandsAsync(client, controllerId);
        var startRuntime = Assert.Single(
            commands,
            command => command.CommandType == ControllerCommandTypes.StartAgentRuntime);
        await PostEventAsync(
            client,
            Event(
                controllerId,
                ControllerEventTypes.AgentRuntimeReady,
                runtimeId: agent.Id,
                commandId: startRuntime.CommandId));
        var sessionResponse = await client.PostAsJsonAsync(
            $"/api/agents/{agent.Id}/sessions",
            new CreateAgentSessionRequest(Goal: "native v1"));
        sessionResponse.EnsureSuccessStatusCode();
        var session = Assert.IsType<AgentSessionRecord>(
            await sessionResponse.Content.ReadFromJsonAsync<AgentSessionRecord>(
                JsonDefaults.Options));
        commands = await PollCommandsAsync(client, controllerId);

        // Assert
        Assert.Contains(
            commands,
            command =>
                command.CommandType == ControllerCommandTypes.CreateAgentSession
                && command.Target.RuntimeId == agent.Id
                && command.Target.AgentSessionId == session.Id);
    }

    [Fact]
    public async Task CommandCallbacks_WhenRetried_ThenReplayOriginalReceipts()
    {
        // Arrange
        using var client = factory.CreateClient();
        const string controllerId = "controller_callback_replay";
        await PostHeartbeatAsync(client, controllerId);
        var agentResponse = await client.PostAsJsonAsync(
            $"/api/agent-controllers/{controllerId}/agents",
            new CreateAgentRequest());
        agentResponse.EnsureSuccessStatusCode();
        var command = Assert.Single(
            await PollCommandsAsync(client, controllerId),
            candidate =>
                candidate.CommandType == ControllerCommandTypes.StartAgentRuntime);
        var acknowledgement = new ControllerCommandAcknowledgement(
            ControllerMessageTypes.CommandAcknowledgement,
            ControllerProtocolVersions.Protocol,
            ControllerProtocolVersions.Schema,
            "ack_callback_1",
            command.CommandId,
            controllerId,
            CommandAcknowledgementStatuses.Accepted,
            DateTimeOffset.UtcNow,
            command.Correlation);

        // Act
        using var firstAckResponse = await client.PostAsJsonAsync(
            $"/api/v1/controllers/{controllerId}/commands/{command.CommandId}" +
            "/acknowledgements",
            acknowledgement,
            ControllerProtocolJson.Options);
        firstAckResponse.EnsureSuccessStatusCode();
        var firstAck = Assert.IsType<ControllerCommandAcknowledgement>(
            await firstAckResponse.Content.ReadFromJsonAsync<
                ControllerCommandAcknowledgement>(
                ControllerProtocolJson.Options));
        using var retryAckResponse = await client.PostAsJsonAsync(
            $"/api/v1/controllers/{controllerId}/commands/{command.CommandId}" +
            "/acknowledgements",
            acknowledgement with
            {
                AcknowledgementId = "ack_callback_2",
                AcknowledgedAt = acknowledgement.AcknowledgedAt.AddSeconds(1)
            },
            ControllerProtocolJson.Options);
        retryAckResponse.EnsureSuccessStatusCode();
        var retryAck = Assert.IsType<ControllerCommandAcknowledgement>(
            await retryAckResponse.Content.ReadFromJsonAsync<
                ControllerCommandAcknowledgement>(
                ControllerProtocolJson.Options));
        var completion = new ControllerCommandCompletion(
            ControllerMessageTypes.CommandCompletion,
            ControllerProtocolVersions.Protocol,
            ControllerProtocolVersions.Schema,
            "completion_callback_1",
            command.CommandId,
            controllerId,
            CommandDeliveryStatuses.Completed,
            DateTimeOffset.UtcNow,
            Assert.IsType<CommandLease>(firstAck.Lease).FencingToken,
            command.Correlation);
        using var firstCompletionResponse = await client.PostAsJsonAsync(
            $"/api/v1/controllers/{controllerId}/commands/{command.CommandId}/completion",
            completion,
            ControllerProtocolJson.Options);
        firstCompletionResponse.EnsureSuccessStatusCode();
        var firstCompletion = Assert.IsType<ControllerCommandCompletion>(
            await firstCompletionResponse.Content.ReadFromJsonAsync<
                ControllerCommandCompletion>(
                ControllerProtocolJson.Options));
        using var retryCompletionResponse = await client.PostAsJsonAsync(
            $"/api/v1/controllers/{controllerId}/commands/{command.CommandId}/completion",
            completion with
            {
                CompletionId = "completion_callback_2",
                CompletedAt = completion.CompletedAt.AddSeconds(1)
            },
            ControllerProtocolJson.Options);
        retryCompletionResponse.EnsureSuccessStatusCode();
        var retryCompletion = Assert.IsType<ControllerCommandCompletion>(
            await retryCompletionResponse.Content.ReadFromJsonAsync<
                ControllerCommandCompletion>(
                ControllerProtocolJson.Options));

        // Assert
        Assert.Equal(firstAck.AcknowledgementId, retryAck.AcknowledgementId);
        Assert.Equal(firstAck.Lease?.LeaseId, retryAck.Lease?.LeaseId);
        Assert.Equal(firstCompletion.CompletionId, retryCompletion.CompletionId);
    }

    [Fact]
    public async Task SubmitTurn_WhenOutcomeEventArrives_ThenProjectsResult()
    {
        // Arrange
        using var client = factory.CreateClient();
        var session = await CreateAgentSessionAsync(client, "controller_turn");
        var turnResponse = await client.PostAsJsonAsync(
            $"/api/agent-sessions/{session.Id}/turns",
            new SubmitTurnRequest("hello controller"));
        turnResponse.EnsureSuccessStatusCode();
        var turn = Assert.IsType<AgentTurnRecord>(
            await turnResponse.Content.ReadFromJsonAsync<AgentTurnRecord>(
                JsonDefaults.Options));
        var controllerId = Assert.IsType<string>(session.ControllerId);
        var commands = await PollCommandsAsync(client, controllerId);
        var invocation = Assert.Single(
            commands,
            command =>
                command.CommandType == ControllerCommandTypes.StartInvocation
                && command.Target.InvocationId == turn.Id);
        var result = new AgentTurnResult(
            "result_1",
            session.Id,
            turn.Id,
            "completed",
            "hello from controller",
            [],
            [],
            [],
            null,
            null);
        var resultRef = await UploadJsonAsync(
            client,
            result,
            "application/vnd.tradecraft.agent-turn-result+json");

        // Act
        await PostEventAsync(
            client,
            Event(
                controllerId,
                ControllerEventTypes.OutcomeReported,
                runtimeId: session.AgentId,
                sessionId: session.Id,
                invocationId: turn.Id,
                commandId: invocation.CommandId,
                payloadRef: resultRef));
        var refreshed = await client.GetFromJsonAsync<AgentTurnRecord>(
            $"/api/agent-sessions/{session.Id}/turns/{turn.Id}",
            JsonDefaults.Options);

        // Assert
        Assert.Equal("completed", Assert.IsType<AgentTurnRecord>(refreshed).Status);
        Assert.Equal("hello from controller", refreshed.Response);
    }

    [Fact]
    public async Task SynchronizeHistory_WhenSnapshotEventArrives_ThenReconcilesTurns()
    {
        // Arrange
        using var client = factory.CreateClient();
        var session = await CreateAgentSessionAsync(client, "controller_history");
        var syncResponse = await client.PostAsync(
            $"/api/agent-sessions/{session.Id}/history/sync",
            content: null);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, syncResponse.StatusCode);
        var controllerId = Assert.IsType<string>(session.ControllerId);
        var commands = await PollCommandsAsync(client, controllerId);
        var sync = Assert.Single(
            commands,
            command =>
                command.CommandType
                == ControllerCommandTypes.SynchronizeSessionHistory);
        var observedAt = DateTimeOffset.UtcNow;
        var history = new AgentChatHistory(
            session.AgentId!,
            session.Id,
            "provider_session_1",
            observedAt,
            [
                new AgentChatMessage(
                    "provider_user_1",
                    "user",
                    "restored prompt",
                    observedAt.AddSeconds(-1),
                    null),
                new AgentChatMessage(
                    "provider_assistant_1",
                    "assistant",
                    "restored response",
                    observedAt,
                    observedAt)
            ]);
        var historyRef = await UploadJsonAsync(
            client,
            history,
            "application/vnd.tradecraft.agent-chat-history+json");

        // Act
        await PostEventAsync(
            client,
            Event(
                controllerId,
                ControllerEventTypes.AgentSessionHistorySynchronized,
                runtimeId: session.AgentId,
                sessionId: session.Id,
                commandId: sync.CommandId,
                payloadRef: historyRef));
        var turns = await client.GetFromJsonAsync<
            IReadOnlyCollection<AgentTurnRecord>>(
            $"/api/agent-sessions/{session.Id}/turns",
            JsonDefaults.Options);

        // Assert
        var restored = Assert.Single(Assert.IsAssignableFrom<
            IReadOnlyCollection<AgentTurnRecord>>(turns));
        Assert.Equal("restored response", restored.Response);
        Assert.Equal("provider_user_1", restored.SourceUserMessageId);
    }

    [Fact]
    public async Task StaleHeartbeat_WhenQueried_ThenControllerIsNotActive()
    {
        // Arrange
        using var client = factory.CreateClient();
        var heartbeat = Heartbeat(
            "controller_stale",
            DateTimeOffset.UtcNow.AddMinutes(-5));

        // Act
        var response = await client.PostAsJsonAsync(
            "/api/v1/controllers/heartbeats",
            heartbeat,
            ControllerProtocolJson.Options);
        response.EnsureSuccessStatusCode();
        var controller = await client.GetAsync(
            "/api/agent-controllers/controller_stale");

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.NotFound, controller.StatusCode);
    }

    [Fact]
    public async Task BrowserLog_WhenPosted_ThenWritesCentralJsonLine()
    {
        // Arrange
        using var client = factory.CreateClient();
        var marker = Ids.New("browser_log");

        // Act
        var response = await client.PostAsJsonAsync(
            "/api/client-logs",
            new ClientLogRequest(
                "error",
                marker,
                DateTimeOffset.UtcNow,
                new Dictionary<string, string?> { ["sessionId"] = "session_1" }));

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        var logPath = Path.Combine(RuntimePaths.LogRoot, "browser.jsonl");
        Assert.Contains(
            File.ReadLines(logPath),
            line => line.Contains(marker, StringComparison.Ordinal));
    }

    private static async Task<AgentSessionRecord> CreateAgentSessionAsync(
        HttpClient client,
        string controllerId)
    {
        await PostHeartbeatAsync(client, controllerId);
        var agentResponse = await client.PostAsJsonAsync(
            $"/api/agent-controllers/{controllerId}/agents",
            new CreateAgentRequest());
        agentResponse.EnsureSuccessStatusCode();
        var agent = Assert.IsType<AgentRecord>(
            await agentResponse.Content.ReadFromJsonAsync<AgentRecord>(
                JsonDefaults.Options));
        await PostEventAsync(
            client,
            Event(
                controllerId,
                ControllerEventTypes.AgentRuntimeReady,
                runtimeId: agent.Id));
        var sessionResponse = await client.PostAsJsonAsync(
            $"/api/agents/{agent.Id}/sessions",
            new CreateAgentSessionRequest());
        sessionResponse.EnsureSuccessStatusCode();
        return Assert.IsType<AgentSessionRecord>(
            await sessionResponse.Content.ReadFromJsonAsync<AgentSessionRecord>(
                JsonDefaults.Options));
    }

    private static async Task<IReadOnlyCollection<ControllerCommand>>
        PollCommandsAsync(HttpClient client, string controllerId)
    {
        var commands = await client.GetFromJsonAsync<
            IReadOnlyCollection<ControllerCommand>>(
            $"/api/v1/controllers/{controllerId}/commands?wait=0",
            ControllerProtocolJson.Options);
        return commands ?? [];
    }

    private static async Task PostHeartbeatAsync(
        HttpClient client,
        string controllerId)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/controllers/heartbeats",
            Heartbeat(controllerId, DateTimeOffset.UtcNow),
            ControllerProtocolJson.Options);
        response.EnsureSuccessStatusCode();
    }

    private static async Task PostEventAsync(
        HttpClient client,
        ControllerEvent controllerEvent)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/controllers/{controllerEvent.ControllerId}/events",
            controllerEvent,
            ControllerProtocolJson.Options);
        response.EnsureSuccessStatusCode();
    }

    private static ControllerHeartbeat Heartbeat(
        string controllerId,
        DateTimeOffset observedAt,
        ImmutableArray<AgentRuntimeResource>? runtimes = null,
        ImmutableArray<AgentSessionResource>? sessions = null)
    {
        return new ControllerHeartbeat(
            ControllerMessageTypes.Heartbeat,
            ControllerProtocolVersions.Protocol,
            ControllerProtocolVersions.Schema,
            controllerId,
            "online",
            observedAt,
            [],
            new ControllerInventory(
                runtimes ?? [],
                sessions ?? []));
    }

    private static AgentRuntimeResource Runtime(
        string controllerId,
        string runtimeId,
        DateTimeOffset observedAt,
        string runtimePath,
        string workspacePath)
    {
        return new AgentRuntimeResource(
            "agent_runtime",
            runtimeId,
            controllerId,
            "ready",
            "opencode",
            "local_process",
            observedAt,
            observedAt,
            Extensions: ImmutableDictionary<string, JsonElement>.Empty.Add(
                "tradecraft.poc",
                JsonSerializer.SerializeToElement(new
                {
                    agent_session_id = runtimeId,
                    runtime_path = runtimePath,
                    workspace_path = workspacePath,
                    opencode_endpoint = "http://127.0.0.1:4097",
                    opencode_pid = 123
                })));
    }

    private static AgentSessionResource Session(
        string runtimeId,
        string sessionId,
        DateTimeOffset observedAt,
        string runtimePath)
    {
        return new AgentSessionResource(
            "agent_session",
            sessionId,
            runtimeId,
            "ready",
            observedAt,
            observedAt,
            ProviderSessionRef: "provider_session_1",
            TranscriptAuthority: "provider",
            Extensions: ImmutableDictionary<string, JsonElement>.Empty.Add(
                "tradecraft.poc",
                JsonSerializer.SerializeToElement(new
                {
                    runtime_path = runtimePath
                })));
    }

    private static ControllerEvent Event(
        string controllerId,
        string eventType,
        string? runtimeId = null,
        string? sessionId = null,
        string? invocationId = null,
        string? commandId = null,
        ContentReference? payloadRef = null)
    {
        var aggregate = invocationId is not null
            ? new AggregateReference("invocation", invocationId)
            : sessionId is not null
                ? new AggregateReference("agent_session", sessionId)
                : new AggregateReference("runtime", runtimeId!);
        return new ControllerEvent(
            ControllerMessageTypes.Event,
            ControllerProtocolVersions.Protocol,
            ControllerProtocolVersions.Schema,
            Ids.New("event"),
            controllerId,
            eventType,
            aggregate,
            1,
            DateTimeOffset.UtcNow,
            ControllerProtocolVersions.Schema,
            new ResourceTarget(
                controllerId,
                runtimeId,
                sessionId,
                invocationId),
            new ProtocolCorrelation(
                CommandId: commandId,
                CausationId: commandId,
                CorrelationId: invocationId ?? sessionId ?? runtimeId,
                InvocationId: invocationId),
            PayloadRef: payloadRef,
            Payload: payloadRef is null
                ? JsonSerializer.SerializeToElement(new { })
                : null);
    }

    private static async Task<ContentReference> UploadJsonAsync<T>(
        HttpClient client,
        T value,
        string contentType)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            value,
            JsonDefaults.Options);
        var upload = new ControllerContentUploadRequest(
            contentType,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            bytes.LongLength,
            Convert.ToBase64String(bytes));
        var response = await client.PostAsJsonAsync(
            "/api/v1/controller-content",
            upload,
            ControllerProtocolJson.Options);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<
            ControllerContentUploadResponse>(ControllerProtocolJson.Options);
        return Assert.IsType<ControllerContentUploadResponse>(body).ContentRef;
    }
}
