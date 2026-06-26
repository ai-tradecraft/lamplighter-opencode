namespace ChatThroughHarness.Api.Tests;

using System.Security.Cryptography;
using ChatThroughHarness.Protocol;
using Xunit;

public sealed class RunnerControlStoreTests
{
    [Fact]
    public async Task CommandCanBePolledClaimedAndCompleted()
    {
        var store = new RunnerControlStore(NewRuntimeRoot());
        var command = new RunnerCommandEnvelope(
            Id: "cmd_1",
            RunnerId: null,
            AgentSessionId: "session_1",
            Type: RunnerCommandTypes.PrepareAgentSession,
            Status: RunnerCommandStatuses.Pending,
            PayloadRef: null,
            CorrelationId: "corr_1",
            IdempotencyKey: "session_1",
            CreatedAt: DateTimeOffset.UnixEpoch,
            AvailableAt: DateTimeOffset.UnixEpoch,
            Lease: null);

        await store.EnqueueCommandAsync(command, CancellationToken.None);

        var available = await store.GetAvailableCommandsAsync("runner_1", CancellationToken.None);
        Assert.Single(available);

        var claimed = await store.ClaimCommandAsync(
            command.Id,
            new ClaimRunnerCommandRequest("runner_1", LeaseSeconds: 30),
            CancellationToken.None);
        Assert.Equal(RunnerCommandMutationStatus.Ok, claimed.Status);
        Assert.Equal(RunnerCommandStatuses.Claimed, claimed.Command?.Status);
        Assert.NotNull(claimed.Command?.Lease);

        var completed = await store.CompleteCommandAsync(
            command.Id,
            new CompleteRunnerCommandRequest(
                RunnerId: "runner_1",
                LeaseId: claimed.Command!.Lease!.LeaseId,
                Status: RunnerCommandStatuses.Completed,
                CompletedAt: DateTimeOffset.UtcNow,
                ResultRef: null,
                Failure: null),
            CancellationToken.None);

        Assert.Equal(RunnerCommandMutationStatus.Ok, completed.Status);
        Assert.Equal(RunnerCommandStatuses.Completed, completed.Command?.Status);
        Assert.Null(completed.Command?.Lease);
    }

    [Fact]
    public async Task EventsAreStoredForDiagnostics()
    {
        var store = new RunnerControlStore(NewRuntimeRoot());
        var runnerEvent = new RunnerEventEnvelope(
            Id: "event_1",
            RunnerId: "runner_1",
            AgentSessionId: "session_1",
            CommandId: "cmd_1",
            Type: RunnerEventTypes.AgentSessionReady,
            PayloadRef: null,
            CausationId: "cmd_1",
            CorrelationId: "corr_1",
            CreatedAt: DateTimeOffset.UnixEpoch);

        await store.AddEventAsync(runnerEvent, CancellationToken.None);

        var events = await store.GetEventsAsync(CancellationToken.None);
        Assert.Single(events);
        Assert.Equal(RunnerEventTypes.AgentSessionReady, events.Single().Type);
    }

    [Fact]
    public async Task ContentUploadValidatesHashAndLength()
    {
        var store = new RunnerControlStore(NewRuntimeRoot());
        var bytes = "hello"u8.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var response = await store.SaveContentAsync(
            new ClaimCheckContentUploadRequest(
                ContentType: "text/plain",
                Sha256: sha,
                Length: bytes.Length,
                ContentBase64: Convert.ToBase64String(bytes)),
            CancellationToken.None);

        Assert.StartsWith("tradecraft://content/", response.ContentRef.Uri);
        Assert.Equal(bytes.Length, response.ContentRef.Length);

        var contentId = response.ContentRef.Uri.Split('/').Last();
        var stored = await store.GetContentAsync(contentId, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(bytes, stored.Value.Bytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveContentAsync(
            new ClaimCheckContentUploadRequest(
                ContentType: "text/plain",
                Sha256: "bad",
                Length: bytes.Length,
                ContentBase64: Convert.ToBase64String(bytes)),
            CancellationToken.None));
    }

    [Fact]
    public async Task StoreReloadsDurableRunnerState()
    {
        var root = NewRuntimeRoot();
        var store = new RunnerControlStore(root);
        var command = new RunnerCommandEnvelope(
            Id: "cmd_1",
            RunnerId: null,
            AgentSessionId: "session_1",
            Type: RunnerCommandTypes.PrepareAgentSession,
            Status: RunnerCommandStatuses.Pending,
            PayloadRef: null,
            CorrelationId: "corr_1",
            IdempotencyKey: "session_1",
            CreatedAt: DateTimeOffset.UnixEpoch,
            AvailableAt: DateTimeOffset.UnixEpoch,
            Lease: null);
        var runnerEvent = new RunnerEventEnvelope(
            Id: "event_1",
            RunnerId: "runner_1",
            AgentSessionId: "session_1",
            CommandId: "cmd_1",
            Type: RunnerEventTypes.AgentSessionReady,
            PayloadRef: null,
            CausationId: "cmd_1",
            CorrelationId: "corr_1",
            CreatedAt: DateTimeOffset.UnixEpoch);
        var heartbeat = new RunnerHeartbeat(
            RunnerId: "runner_1",
            Status: "online",
            ActiveCommandIds: ["cmd_1"],
            ObservedAt: DateTimeOffset.UnixEpoch,
            Agents: []);
        var bytes = "persisted"u8.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        await store.EnqueueCommandAsync(command, CancellationToken.None);
        await store.AddEventAsync(runnerEvent, CancellationToken.None);
        await store.UpsertRunnerHeartbeatAsync(heartbeat, CancellationToken.None);
        var content = await store.SaveContentAsync(
            new ClaimCheckContentUploadRequest("text/plain", sha, bytes.Length, Convert.ToBase64String(bytes)),
            CancellationToken.None);

        var reloaded = new RunnerControlStore(root);

        Assert.Single(await reloaded.GetAvailableCommandsAsync("runner_1", CancellationToken.None));
        Assert.Single(await reloaded.GetEventsAsync(CancellationToken.None));
        Assert.Equal("online", (await reloaded.GetRunnerHeartbeatAsync("runner_1", CancellationToken.None))?.Status);
        var contentId = content.ContentRef.Uri.Split('/').Last();
        Assert.Equal(bytes, (await reloaded.GetContentAsync(contentId, CancellationToken.None))?.Bytes);
    }

    private static string NewRuntimeRoot()
    {
        return Path.Combine(Path.GetTempPath(), Ids.New("runner_runtime"));
    }
}
