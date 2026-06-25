namespace ChatThroughHarness.Api.Tests;

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
    }

    private static async Task<IReadOnlyCollection<RunnerCommandEnvelope>> PollCommandsAsync(HttpClient client)
    {
        var commands = await client.GetFromJsonAsync<IReadOnlyCollection<RunnerCommandEnvelope>>(
            "/api/runner/commands?runnerId=runner_test&wait=0",
            RunnerProtocolJson.Options);
        return commands ?? [];
    }

    private static async Task<string> ReadContentAsync(HttpClient client, ClaimCheckContentRef contentRef)
    {
        var contentId = contentRef.Uri.Split('/').Last();
        return await client.GetStringAsync($"/api/runner/content/{contentId}");
    }
}
