namespace ChatThroughHarness.Api.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using ChatThroughHarness.Protocol;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

public sealed class RunnerCommandEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RunnerCommandEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CreateSessionQueuesPrepareCommand()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/agent-sessions", new CreateAgentSessionRequest());
        response.EnsureSuccessStatusCode();
        var session = await response.Content.ReadFromJsonAsync<AgentSessionRecord>(JsonDefaults.Options);
        Assert.NotNull(session);
        Assert.Equal("preparing", session.Status);

        var commands = await PollCommandsAsync(client);
        var command = Assert.Single(commands, command =>
            command.AgentSessionId == session.Id &&
            command.Type == RunnerCommandTypes.PrepareAgentSession);

        Assert.Equal(RunnerCommandStatuses.Pending, command.Status);
        Assert.NotNull(command.PayloadRef);

        var payload = await ReadContentAsync(client, command.PayloadRef!);
        Assert.Contains("\"agent_session_id\"", payload);
        Assert.Contains(session.Id, payload);
    }

    [Fact]
    public async Task CreateSessionForControllerQueuesCommandForThatRunner()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/agent-sessions",
            new CreateAgentSessionRequest(ControllerId: "runner_local"));
        response.EnsureSuccessStatusCode();
        var session = await response.Content.ReadFromJsonAsync<AgentSessionRecord>(JsonDefaults.Options);
        Assert.NotNull(session);
        Assert.Equal("runner_local", session.ControllerId);

        var otherRunnerCommands = await PollCommandsAsync(client, "runner_other");
        Assert.DoesNotContain(otherRunnerCommands, command => command.AgentSessionId == session.Id);

        var commands = await PollCommandsAsync(client, "runner_local");
        var command = Assert.Single(commands, command => command.AgentSessionId == session.Id);
        Assert.Equal("runner_local", command.RunnerId);
    }

    [Fact]
    public async Task RunnerHeartbeatRegistersControllerAndAgentInventory()
    {
        using var client = _factory.CreateClient();
        var observedAt = DateTimeOffset.UtcNow;
        var heartbeat = new RunnerHeartbeat(
            RunnerId: "runner_local",
            Status: "online",
            ActiveCommandIds: [],
            ObservedAt: observedAt,
            Agents:
            [
                new RunnerAgentInventoryItem(
                    AgentSessionId: "session_local",
                    Status: "ready",
                    RuntimePath: "/tmp/session_local",
                    WorkspacePath: "/tmp/session_local/workspace",
                    OpenCodeEndpoint: "http://127.0.0.1:4097",
                    OpenCodePid: 123,
                    ObservedAt: observedAt)
            ]);

        var response = await client.PostAsJsonAsync("/api/runner/heartbeat", heartbeat, RunnerProtocolJson.Options);
        response.EnsureSuccessStatusCode();

        var controllersResponse = await client.GetAsync("/api/agent-controllers");
        controllersResponse.EnsureSuccessStatusCode();
        var controllersJson = await controllersResponse.Content.ReadAsStringAsync();
        Assert.Contains("\"agentSessionId\"", controllersJson);
        Assert.DoesNotContain("\"agent_session_id\"", controllersJson);
        var controllers = JsonSerializer.Deserialize<IReadOnlyCollection<AgentControllerRecord>>(
            controllersJson,
            JsonDefaults.Options);
        Assert.NotNull(controllers);
        var controller = Assert.Single(controllers, item => item.RunnerId == "runner_local");
        Assert.Equal("online", controller.Status);
        var agent = Assert.Single(controller.Agents);
        Assert.Equal("session_local", agent.AgentSessionId);

        var session = await client.GetFromJsonAsync<AgentSessionRecord>(
            "/api/agent-sessions/session_local",
            JsonDefaults.Options);
        Assert.NotNull(session);
        Assert.Equal("ready", session.Status);
        Assert.Equal("runner_local", session.ControllerId);
    }

    [Fact]
    public async Task SubmitTurnQueuesTurnCommandWithoutHarnessResponse()
    {
        using var client = _factory.CreateClient();
        var sessionResponse = await client.PostAsJsonAsync("/api/agent-sessions", new CreateAgentSessionRequest());
        sessionResponse.EnsureSuccessStatusCode();
        var session = await sessionResponse.Content.ReadFromJsonAsync<AgentSessionRecord>(JsonDefaults.Options);
        Assert.NotNull(session);

        var turnResponse = await client.PostAsJsonAsync(
            $"/api/agent-sessions/{session.Id}/turns",
            new SubmitTurnRequest("hello runner"));
        turnResponse.EnsureSuccessStatusCode();
        var turn = await turnResponse.Content.ReadFromJsonAsync<AgentTurnRecord>(JsonDefaults.Options);
        Assert.NotNull(turn);
        Assert.Equal("submitted", turn.Status);
        Assert.Null(turn.Response);

        var commands = await PollCommandsAsync(client);
        var command = Assert.Single(commands, command =>
            command.AgentSessionId == session.Id &&
            command.Type == RunnerCommandTypes.SubmitAgentTurn);

        Assert.Equal(turn.Id, command.CorrelationId);
        Assert.NotNull(command.PayloadRef);

        var payload = await ReadContentAsync(client, command.PayloadRef!);
        Assert.Contains("\"instruction\"", payload);
        Assert.Contains("hello runner", payload);

        var turns = await client.GetFromJsonAsync<IReadOnlyCollection<AgentTurnRecord>>(
            $"/api/agent-sessions/{session.Id}/turns",
            JsonDefaults.Options);
        Assert.NotNull(turns);
        Assert.Contains(turns, item => item.Id == turn.Id);
    }

    [Fact]
    public async Task RunnerReadyEventUpdatesSessionStatus()
    {
        using var client = _factory.CreateClient();
        var sessionResponse = await client.PostAsJsonAsync("/api/agent-sessions", new CreateAgentSessionRequest());
        sessionResponse.EnsureSuccessStatusCode();
        var session = await sessionResponse.Content.ReadFromJsonAsync<AgentSessionRecord>(JsonDefaults.Options);
        Assert.NotNull(session);

        var runnerEvent = new RunnerEventEnvelope(
            Id: "event_ready",
            RunnerId: "runner_test",
            AgentSessionId: session.Id,
            CommandId: "cmd_prepare",
            Type: RunnerEventTypes.AgentSessionReady,
            PayloadRef: null,
            CausationId: "cmd_prepare",
            CorrelationId: session.Id,
            CreatedAt: DateTimeOffset.UtcNow);

        var eventResponse = await client.PostAsJsonAsync("/api/runner/events", runnerEvent, RunnerProtocolJson.Options);
        eventResponse.EnsureSuccessStatusCode();

        var refreshed = await client.GetFromJsonAsync<AgentSessionRecord>(
            $"/api/agent-sessions/{session.Id}",
            JsonDefaults.Options);
        Assert.NotNull(refreshed);
        Assert.Equal("ready", refreshed.Status);
        Assert.NotNull(refreshed.ReadyAt);
    }

    [Fact]
    public async Task RunnerTurnCompletedEventUpdatesTurnResponse()
    {
        using var client = _factory.CreateClient();
        var sessionResponse = await client.PostAsJsonAsync("/api/agent-sessions", new CreateAgentSessionRequest());
        sessionResponse.EnsureSuccessStatusCode();
        var session = await sessionResponse.Content.ReadFromJsonAsync<AgentSessionRecord>(JsonDefaults.Options);
        Assert.NotNull(session);

        var turnResponse = await client.PostAsJsonAsync(
            $"/api/agent-sessions/{session.Id}/turns",
            new SubmitTurnRequest("hello runner"));
        turnResponse.EnsureSuccessStatusCode();
        var turn = await turnResponse.Content.ReadFromJsonAsync<AgentTurnRecord>(JsonDefaults.Options);
        Assert.NotNull(turn);

        var result = new AgentTurnResult(
            Id: "result_1",
            AgentSessionId: session.Id,
            RequestId: turn.Id,
            Status: "completed",
            Message: "hello from runner",
            ArtifactRefs: [],
            ChangedFiles: [],
            CommandsObserved: [],
            FailureReport: null,
            Diagnostics: null);
        var payloadRef = await UploadJsonAsync(client, result, "application/vnd.tradecraft.agent-turn-result+json");
        var runnerEvent = new RunnerEventEnvelope(
            Id: "event_turn_completed",
            RunnerId: "runner_test",
            AgentSessionId: session.Id,
            CommandId: "cmd_turn",
            Type: RunnerEventTypes.AgentTurnCompleted,
            PayloadRef: payloadRef,
            CausationId: "cmd_turn",
            CorrelationId: turn.Id,
            CreatedAt: DateTimeOffset.UtcNow);

        var eventResponse = await client.PostAsJsonAsync("/api/runner/events", runnerEvent, RunnerProtocolJson.Options);
        eventResponse.EnsureSuccessStatusCode();

        var refreshed = await client.GetFromJsonAsync<AgentTurnRecord>(
            $"/api/agent-sessions/{session.Id}/turns/{turn.Id}",
            JsonDefaults.Options);
        Assert.NotNull(refreshed);
        Assert.Equal("completed", refreshed.Status);
        Assert.Equal("hello from runner", refreshed.Response);

        var events = await client.GetFromJsonAsync<IReadOnlyCollection<RuntimeEventRecord>>(
            $"/api/agent-sessions/{session.Id}/events",
            JsonDefaults.Options);
        Assert.NotNull(events);
        var completedEvent = Assert.Single(events, item => item.Type == RunnerEventTypes.AgentTurnCompleted);
        Assert.Equal(turn.Id, completedEvent.TurnId);
    }

    private static async Task<IReadOnlyCollection<RunnerCommandEnvelope>> PollCommandsAsync(
        HttpClient client,
        string runnerId = "runner_test")
    {
        var commands = await client.GetFromJsonAsync<IReadOnlyCollection<RunnerCommandEnvelope>>(
            $"/api/runner/commands?runnerId={runnerId}&wait=0",
            RunnerProtocolJson.Options);
        return commands ?? [];
    }

    private static async Task<string> ReadContentAsync(HttpClient client, ClaimCheckContentRef contentRef)
    {
        var contentId = contentRef.Uri.Split('/').Last();
        return await client.GetStringAsync($"/api/runner/content/{contentId}");
    }

    private static async Task<ClaimCheckContentRef> UploadJsonAsync<T>(HttpClient client, T value, string contentType)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.Options);
        var upload = new ClaimCheckContentUploadRequest(
            ContentType: contentType,
            Sha256: Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            Length: bytes.LongLength,
            ContentBase64: Convert.ToBase64String(bytes));
        var response = await client.PostAsJsonAsync("/api/runner/content", upload, RunnerProtocolJson.Options);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ClaimCheckContentUploadResponse>(RunnerProtocolJson.Options);
        Assert.NotNull(body);
        return body.ContentRef;
    }
}
