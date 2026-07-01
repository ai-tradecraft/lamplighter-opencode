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
}
