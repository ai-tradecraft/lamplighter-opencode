namespace ChatThroughHarness.Api.Tests;

using Xunit;

public sealed class HarnessClientTests
{
    [Fact]
    public async Task FakeHarnessReturnsDeterministicResponse()
    {
        var client = new FakeHarnessClient();
        var session = AgentSessionRecord.Create(new CreateAgentSessionRequest(), Path.Combine(Path.GetTempPath(), Ids.New("runtime")));
        var turn = AgentTurnRecord.Create(session.Id, "hello");

        var result = await client.SubmitTurnAsync(session, turn, CancellationToken.None);

        Assert.Equal("completed", result.Status);
        Assert.Contains("Fake harness response", result.Message);
        Assert.Equal(session.Id, result.AgentSessionId);
    }

    [Fact]
    public async Task StorePersistsSessionAndTurn()
    {
        var store = new AgentSessionStore();
        var session = AgentSessionRecord.Create(new CreateAgentSessionRequest(), Path.Combine(Path.GetTempPath(), Ids.New("runtime")));
        var turn = AgentTurnRecord.Create(session.Id, "hello");

        await store.UpsertSessionAsync(session, CancellationToken.None);
        await store.UpsertTurnAsync(turn, CancellationToken.None);

        Assert.Equal(session.Id, (await store.GetSessionAsync(session.Id, CancellationToken.None))?.Id);
        Assert.Equal(turn.Id, (await store.GetTurnAsync(session.Id, turn.Id, CancellationToken.None))?.Id);
    }
}
