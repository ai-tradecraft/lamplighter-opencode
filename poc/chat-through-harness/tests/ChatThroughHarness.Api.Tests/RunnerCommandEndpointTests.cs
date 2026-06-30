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
    public async Task ControllerAgentOwnsMultipleSessions()
    {
        using var client = _factory.CreateClient();
        var runnerId = Ids.New("runner");
        await PostHeartbeatAsync(client, runnerId);

        var agentResponse = await client.PostAsJsonAsync(
            $"/api/agent-controllers/{runnerId}/agents",
            new CreateAgentRequest());
        agentResponse.EnsureSuccessStatusCode();
        var agent = await agentResponse.Content.ReadFromJsonAsync<AgentRecord>(JsonDefaults.Options);
        Assert.NotNull(agent);
        Assert.StartsWith("agent_", agent.Id);

        var commands = await PollCommandsAsync(client, runnerId);
        var prepareAgent = Assert.Single(commands, command =>
            command.AgentId == agent.Id
            && command.Type == RunnerCommandTypes.PrepareAgent);
        Assert.Null(prepareAgent.SessionId);

        var readyEvent = new RunnerEventEnvelope(
            Id: Ids.New("event"),
            RunnerId: runnerId,
            AgentSessionId: agent.Id,
            CommandId: prepareAgent.Id,
            Type: RunnerEventTypes.AgentReady,
            PayloadRef: null,
            CausationId: prepareAgent.Id,
            CorrelationId: agent.Id,
            CreatedAt: DateTimeOffset.UtcNow,
            AgentId: agent.Id);
        (await client.PostAsJsonAsync(
            "/api/runner/events",
            readyEvent,
            RunnerProtocolJson.Options)).EnsureSuccessStatusCode();

        var sessions = new List<AgentSessionRecord>();
        for (var index = 0; index < 2; index++)
        {
            var response = await client.PostAsJsonAsync(
                $"/api/agents/{agent.Id}/sessions",
                new CreateAgentSessionRequest(Goal: $"chat {index}"));
            response.EnsureSuccessStatusCode();
            sessions.Add((await response.Content.ReadFromJsonAsync<AgentSessionRecord>(JsonDefaults.Options))!);
        }

        Assert.All(sessions, session => Assert.Equal(agent.Id, session.AgentId));
        Assert.NotEqual(sessions[0].Id, sessions[1].Id);
        var listed = await client.GetFromJsonAsync<IReadOnlyCollection<AgentSessionRecord>>(
            $"/api/agents/{agent.Id}/sessions",
            JsonDefaults.Options);
        Assert.NotNull(listed);
        Assert.Equal(2, listed.Count);

        commands = await PollCommandsAsync(client, runnerId);
        Assert.Contains(commands, command =>
            command.AgentId == agent.Id
            && command.SessionId == sessions[0].Id
            && command.Type == RunnerCommandTypes.CreateAgentSession);
        Assert.Contains(commands, command =>
            command.AgentId == agent.Id
            && command.SessionId == sessions[1].Id
            && command.Type == RunnerCommandTypes.CreateAgentSession);
    }

    [Fact]
    public async Task SessionCreationRejectsStaleOwningController()
    {
        using var client = _factory.CreateClient();
        var runnerId = Ids.New("runner");
        await PostHeartbeatAsync(client, runnerId);
        var agentResponse = await client.PostAsJsonAsync(
            $"/api/agent-controllers/{runnerId}/agents",
            new CreateAgentRequest());
        var agent = await agentResponse.Content.ReadFromJsonAsync<AgentRecord>(JsonDefaults.Options);
        Assert.NotNull(agent);
        var readyEvent = new RunnerEventEnvelope(
            Id: Ids.New("event"),
            RunnerId: runnerId,
            AgentSessionId: agent.Id,
            CommandId: null,
            Type: RunnerEventTypes.AgentReady,
            PayloadRef: null,
            CausationId: null,
            CorrelationId: agent.Id,
            CreatedAt: DateTimeOffset.UtcNow,
            AgentId: agent.Id);
        (await client.PostAsJsonAsync(
            "/api/runner/events",
            readyEvent,
            RunnerProtocolJson.Options)).EnsureSuccessStatusCode();
        var staleHeartbeat = new RunnerHeartbeat(
            RunnerId: runnerId,
            Status: "offline",
            ActiveCommandIds: [],
            ObservedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            Agents: []);
        (await client.PostAsJsonAsync(
            "/api/runner/heartbeat",
            staleHeartbeat,
            RunnerProtocolJson.Options)).EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync(
            $"/api/agents/{agent.Id}/sessions",
            new CreateAgentSessionRequest());

        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task BrowserLogsAreWrittenToCentralJsonLinesFile()
    {
        using var client = _factory.CreateClient();
        var marker = Ids.New("browser_log");
        var response = await client.PostAsJsonAsync(
            "/api/client-logs",
            new ClientLogRequest(
                Level: "error",
                Message: marker,
                Timestamp: DateTimeOffset.UtcNow,
                Context: new Dictionary<string, string?> { ["sessionId"] = "session_1" }));

        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        var logPath = Path.Combine(RuntimePaths.LogRoot, "browser.jsonl");
        var contents = await File.ReadAllTextAsync(logPath);
        Assert.Contains(marker, contents);
        Assert.Contains("\"source\":\"browser\"", contents);
        Assert.Contains(
            File.ReadLines(logPath),
            line => line.Contains(marker, StringComparison.Ordinal));
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
        await PostHeartbeatAsync(client, "runner_local");

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
    public async Task StaleRunnerHeartbeatIsNotReturnedAsActiveController()
    {
        using var client = _factory.CreateClient();
        var heartbeat = new RunnerHeartbeat(
            RunnerId: "runner_stale",
            Status: "online",
            ActiveCommandIds: [],
            ObservedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            Agents: []);

        var response = await client.PostAsJsonAsync(
            "/api/runner/heartbeat",
            heartbeat,
            RunnerProtocolJson.Options);
        response.EnsureSuccessStatusCode();

        var controllers = await client.GetFromJsonAsync<IReadOnlyCollection<AgentControllerRecord>>(
            "/api/agent-controllers",
            JsonDefaults.Options);
        Assert.NotNull(controllers);
        Assert.DoesNotContain(controllers, item => item.RunnerId == "runner_stale");

        var staleController = await client.GetAsync("/api/agent-controllers/runner_stale");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, staleController.StatusCode);

        var createResponse = await client.PostAsJsonAsync(
            "/api/agent-sessions",
            new CreateAgentSessionRequest(ControllerId: "runner_stale"));
        Assert.Equal(System.Net.HttpStatusCode.Conflict, createResponse.StatusCode);
    }

    [Fact]
    public async Task RunnerHeartbeatDoesNotOverwriteCancelledSessionStatus()
    {
        using var client = _factory.CreateClient();
        await PostHeartbeatAsync(client, "runner_terminal");
        var sessionResponse = await client.PostAsJsonAsync(
            "/api/agent-sessions",
            new CreateAgentSessionRequest(ControllerId: "runner_terminal"));
        sessionResponse.EnsureSuccessStatusCode();
        var session = await sessionResponse.Content.ReadFromJsonAsync<AgentSessionRecord>(JsonDefaults.Options);
        Assert.NotNull(session);

        var cancelledEvent = new RunnerEventEnvelope(
            Id: "event_cancelled",
            RunnerId: "runner_terminal",
            AgentSessionId: session.Id,
            CommandId: "cmd_cancel",
            Type: "agent_session.cancelled",
            PayloadRef: null,
            CausationId: "cmd_cancel",
            CorrelationId: session.Id,
            CreatedAt: DateTimeOffset.UtcNow);
        var eventResponse = await client.PostAsJsonAsync(
            "/api/runner/events",
            cancelledEvent,
            RunnerProtocolJson.Options);
        eventResponse.EnsureSuccessStatusCode();

        var heartbeat = new RunnerHeartbeat(
            RunnerId: "runner_terminal",
            Status: "online",
            ActiveCommandIds: [],
            ObservedAt: DateTimeOffset.UtcNow,
            Agents:
            [
                new RunnerAgentInventoryItem(
                    AgentSessionId: session.Id,
                    Status: "ready",
                    RuntimePath: session.RuntimePath,
                    WorkspacePath: session.WorkspacePath,
                    OpenCodeEndpoint: "http://127.0.0.1:4097",
                    OpenCodePid: 123,
                    ObservedAt: DateTimeOffset.UtcNow)
            ]);
        var heartbeatResponse = await client.PostAsJsonAsync(
            "/api/runner/heartbeat",
            heartbeat,
            RunnerProtocolJson.Options);
        heartbeatResponse.EnsureSuccessStatusCode();

        var refreshed = await client.GetFromJsonAsync<AgentSessionRecord>(
            $"/api/agent-sessions/{session.Id}",
            JsonDefaults.Options);
        Assert.NotNull(refreshed);
        Assert.Equal("cancelled", refreshed.Status);
        Assert.NotNull(refreshed.EndedAt);
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

    [Fact]
    public async Task RunnerTurnFailedEventProjectsStructuredFailureDetails()
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
            Id: "result_failed",
            AgentSessionId: session.Id,
            RequestId: turn.Id,
            Status: "failed",
            Message: "OpenCode server request failed.",
            ArtifactRefs: [],
            ChangedFiles: [],
            CommandsObserved: [],
            FailureReport: new FailureReport(
                Summary: "OpenCode server request failed.",
                Detail: "Connection refused"),
            Diagnostics: null);
        var payloadRef = await UploadJsonAsync(client, result, "application/vnd.tradecraft.agent-turn-result+json");
        var runnerEvent = new RunnerEventEnvelope(
            Id: "event_turn_failed",
            RunnerId: "runner_test",
            AgentSessionId: session.Id,
            CommandId: "cmd_turn",
            Type: RunnerEventTypes.AgentTurnFailed,
            PayloadRef: payloadRef,
            CausationId: "cmd_turn",
            CorrelationId: turn.Id,
            CreatedAt: DateTimeOffset.UtcNow);

        var eventResponse = await client.PostAsJsonAsync(
            "/api/runner/events",
            runnerEvent,
            RunnerProtocolJson.Options);
        eventResponse.EnsureSuccessStatusCode();

        var refreshed = await client.GetFromJsonAsync<AgentTurnRecord>(
            $"/api/agent-sessions/{session.Id}/turns/{turn.Id}",
            JsonDefaults.Options);
        Assert.NotNull(refreshed);
        Assert.Equal("failed", refreshed.Status);
        Assert.Equal("OpenCode server request failed.", refreshed.FailureSummary);
        Assert.Equal("Connection refused", refreshed.FailureDetail);
    }

    [Fact]
    public async Task RunnerTurnFailedEventAcceptsPlainTextProcessDiagnostics()
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

        var payloadRef = await UploadTextAsync(client, "exit_code: 2\nstderr: boom", "text/plain");
        var runnerEvent = new RunnerEventEnvelope(
            Id: "event_turn_process_failed",
            RunnerId: "runner_test",
            AgentSessionId: session.Id,
            CommandId: "cmd_turn",
            Type: RunnerEventTypes.AgentTurnFailed,
            PayloadRef: payloadRef,
            CausationId: "cmd_turn",
            CorrelationId: turn.Id,
            CreatedAt: DateTimeOffset.UtcNow);

        var eventResponse = await client.PostAsJsonAsync(
            "/api/runner/events",
            runnerEvent,
            RunnerProtocolJson.Options);
        eventResponse.EnsureSuccessStatusCode();

        var refreshed = await client.GetFromJsonAsync<AgentTurnRecord>(
            $"/api/agent-sessions/{session.Id}/turns/{turn.Id}",
            JsonDefaults.Options);
        Assert.NotNull(refreshed);
        Assert.Equal("failed", refreshed.Status);
        Assert.Contains("exit_code: 2", refreshed.FailureSummary);
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

    private static async Task PostHeartbeatAsync(HttpClient client, string runnerId)
    {
        var heartbeat = new RunnerHeartbeat(
            RunnerId: runnerId,
            Status: "online",
            ActiveCommandIds: [],
            ObservedAt: DateTimeOffset.UtcNow,
            Agents: []);
        var response = await client.PostAsJsonAsync(
            "/api/runner/heartbeat",
            heartbeat,
            RunnerProtocolJson.Options);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<string> ReadContentAsync(HttpClient client, ClaimCheckContentRef contentRef)
    {
        var contentId = contentRef.Uri.Split('/').Last();
        return await client.GetStringAsync($"/api/runner/content/{contentId}");
    }

    private static async Task<ClaimCheckContentRef> UploadJsonAsync<T>(HttpClient client, T value, string contentType)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.Options);
        return await UploadBytesAsync(client, bytes, contentType);
    }

    private static Task<ClaimCheckContentRef> UploadTextAsync(
        HttpClient client,
        string value,
        string contentType)
    {
        return UploadBytesAsync(client, System.Text.Encoding.UTF8.GetBytes(value), contentType);
    }

    private static async Task<ClaimCheckContentRef> UploadBytesAsync(
        HttpClient client,
        byte[] bytes,
        string contentType)
    {
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
