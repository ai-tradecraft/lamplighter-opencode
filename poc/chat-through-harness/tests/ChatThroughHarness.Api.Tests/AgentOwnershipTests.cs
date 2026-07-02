namespace ChatThroughHarness.Api.Tests;

using Xunit;

public sealed class AgentOwnershipTests
{
    [Fact]
    public async Task InventoryFromDifferentControllerCannotStealAgent()
    {
        var store = new AgentStore();
        var agent = AgentRecord.Create("runner_owner", new CreateAgentRequest());
        await store.UpsertAgentAsync(agent, CancellationToken.None);
        var inventory = new ControllerInventoryObservation(
            AgentSessionId: agent.Id,
            Status: "ready",
            RuntimePath: "/runtime",
            WorkspacePath: "/workspace",
            OpenCodeEndpoint: "http://127.0.0.1:4096",
            OpenCodePid: 123,
            ObservedAt: DateTimeOffset.UtcNow,
            AgentId: agent.Id,
            Sessions: []);

        var accepted = await store.UpsertInventoryAgentAsync(
            "runner_other",
            inventory,
            CancellationToken.None);

        Assert.False(accepted);
        var unchanged = await store.GetAgentAsync(agent.Id, CancellationToken.None);
        Assert.NotNull(unchanged);
        Assert.Equal("runner_owner", unchanged.ControllerId);
    }

    [Fact]
    public async Task ClosingAgentSessionsCancelsOnlyNonterminalChildren()
    {
        var store = new AgentSessionStore();
        var agent = AgentRecord.Create("runner_owner", new CreateAgentRequest());
        var ready = AgentSessionRecord.CreateForAgent(agent, new CreateAgentSessionRequest())
            with { Status = "ready" };
        var preparing = AgentSessionRecord.CreateForAgent(agent, new CreateAgentSessionRequest())
            with { Status = "preparing" };
        var failed = AgentSessionRecord.CreateForAgent(agent, new CreateAgentSessionRequest())
            with { Status = "failed", FailedAt = DateTimeOffset.UtcNow };
        var otherAgent = AgentRecord.Create("runner_owner", new CreateAgentRequest());
        var unrelated = AgentSessionRecord.CreateForAgent(otherAgent, new CreateAgentSessionRequest())
            with { Status = "ready" };
        await store.UpsertSessionAsync(ready, CancellationToken.None);
        await store.UpsertSessionAsync(preparing, CancellationToken.None);
        await store.UpsertSessionAsync(failed, CancellationToken.None);
        await store.UpsertSessionAsync(unrelated, CancellationToken.None);
        var stoppedAt = DateTimeOffset.UtcNow;

        await store.CloseSessionsForAgentAsync(
            agent.Id,
            stoppedAt,
            "Parent agent stopped.",
            CancellationToken.None);

        var closedReady = await store.GetSessionAsync(ready.Id, CancellationToken.None);
        var closedPreparing = await store.GetSessionAsync(preparing.Id, CancellationToken.None);
        var unchangedFailed = await store.GetSessionAsync(failed.Id, CancellationToken.None);
        var unchangedUnrelated = await store.GetSessionAsync(unrelated.Id, CancellationToken.None);
        Assert.Equal("cancelled", closedReady?.Status);
        Assert.Equal("cancelled", closedPreparing?.Status);
        Assert.Equal(stoppedAt, closedReady?.EndedAt);
        Assert.Equal("Parent agent stopped.", closedReady?.FailureSummary);
        Assert.Equal("failed", unchangedFailed?.Status);
        Assert.Equal("ready", unchangedUnrelated?.Status);
    }

    [Fact]
    public void AgentSpecFromAgentDoesNotEmitProviderOrModelConfiguration()
    {
        var agent = AgentRecord.Create("runner_owner", new CreateAgentRequest());

        var spec = AgentSpec.FromAgent(agent);

        Assert.Equal("opencode", spec.Backend.Kind);
        Assert.Empty(spec.Backend.Config);
        Assert.Empty(spec.Backend.RequiredEnvironmentVariables);
    }
}
