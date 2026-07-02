namespace ChatThroughHarness.Api.Tests;

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using ChatThroughHarness.Protocol.V1;
using Xunit;

public sealed class RunnerControlStoreTests
{
    [Fact]
    public async Task AcknowledgeCommandAsync_WhenCommandIsAvailable_ThenAddsFencedLease()
    {
        // Arrange
        var store = new RunnerControlStore(NewRuntimeRoot());
        var command = Command();
        await store.EnqueueCommandAsync(command, CancellationToken.None);

        // Act
        var available = await store.GetAvailableCommandsAsync(
            "controller_1",
            CancellationToken.None);
        var acknowledged = await store.AcknowledgeCommandAsync(
            Acknowledgement(command),
            CancellationToken.None);

        // Assert
        Assert.Single(available);
        Assert.Equal(ControllerCommandMutationStatus.Ok, acknowledged.Status);
        var lease = Assert.IsType<CommandLease>(
            Assert.IsType<ControllerCommand>(acknowledged.Command).Execution.Lease);
        Assert.Same(
            lease,
            Assert.IsType<ControllerCommandAcknowledgement>(
                acknowledged.Acknowledgement).Lease);
        Assert.Equal("controller_1", lease.ControllerId);
        Assert.Equal(1, lease.Attempt);
        Assert.Equal(1, lease.FencingToken);
    }

    [Fact]
    public async Task EnqueueCommandAsync_WhenEquivalentRequestRepeats_ThenReplaysOriginal()
    {
        // Arrange
        var store = new RunnerControlStore(NewRuntimeRoot());
        var original = Command();
        var retry = EquivalentRetry(original, "cmd_2");

        // Act
        var accepted = await store.EnqueueCommandAsync(
            original,
            CancellationToken.None);
        var replayed = await store.EnqueueCommandAsync(
            retry,
            CancellationToken.None);

        // Assert
        Assert.Equal(ControllerCommandEnqueueStatus.Accepted, accepted.Status);
        Assert.Equal(ControllerCommandEnqueueStatus.Replayed, replayed.Status);
        Assert.Equal(original.CommandId, replayed.Command?.CommandId);
        Assert.Single(
            await store.GetAvailableCommandsAsync(
                "controller_1",
                CancellationToken.None));
    }

    [Fact]
    public async Task EnqueueCommandAsync_WhenKeyIsReusedForDifferentRequest_ThenConflicts()
    {
        // Arrange
        var store = new RunnerControlStore(NewRuntimeRoot());
        var original = Command();
        await store.EnqueueCommandAsync(original, CancellationToken.None);
        var conflicting = EquivalentRetry(original, "cmd_2") with
        {
            Payload = JsonSerializer.SerializeToElement(new { value = "different" })
        };

        // Act
        var result = await store.EnqueueCommandAsync(
            conflicting,
            CancellationToken.None);

        // Assert
        Assert.Equal(ControllerCommandEnqueueStatus.Conflict, result.Status);
        Assert.Contains("different", result.Message);
    }

    [Fact]
    public async Task EnqueueCommandAsync_WhenEquivalentRequestsRace_ThenAcceptsOnlyOne()
    {
        // Arrange
        var store = new RunnerControlStore(NewRuntimeRoot());
        var original = Command();
        var requests = Enumerable.Range(1, 20)
            .Select(index => EquivalentRetry(original, $"cmd_{index}"))
            .ToArray();

        // Act
        var results = await Task.WhenAll(
            requests.Select(
                request => store.EnqueueCommandAsync(
                    request,
                    CancellationToken.None)));

        // Assert
        Assert.Single(
            results,
            result => result.Status == ControllerCommandEnqueueStatus.Accepted);
        Assert.Equal(
            19,
            results.Count(
                result => result.Status == ControllerCommandEnqueueStatus.Replayed));
        Assert.Single(results.Select(result => result.Command?.CommandId).Distinct());
    }

    [Fact]
    public async Task AcknowledgeCommandAsync_WhenEquivalentRequestRepeats_ThenReplaysLease()
    {
        // Arrange
        var store = new RunnerControlStore(NewRuntimeRoot());
        var command = Command();
        await store.EnqueueCommandAsync(command, CancellationToken.None);
        var firstRequest = Acknowledgement(command);

        // Act
        var first = await store.AcknowledgeCommandAsync(
            firstRequest,
            CancellationToken.None);
        var replayed = await store.AcknowledgeCommandAsync(
            firstRequest with
            {
                AcknowledgementId = "ack_2",
                AcknowledgedAt = firstRequest.AcknowledgedAt.AddMinutes(1)
            },
            CancellationToken.None);

        // Assert
        Assert.Equal(ControllerCommandMutationStatus.Ok, replayed.Status);
        Assert.Equal(
            first.Acknowledgement?.AcknowledgementId,
            replayed.Acknowledgement?.AcknowledgementId);
        Assert.Equal(
            first.Acknowledgement?.Lease?.LeaseId,
            replayed.Acknowledgement?.Lease?.LeaseId);
    }

    [Fact]
    public async Task AcknowledgeCommandAsync_WhenDuplicateDiffers_ThenConflicts()
    {
        // Arrange
        var store = new RunnerControlStore(NewRuntimeRoot());
        var command = Command();
        await store.EnqueueCommandAsync(command, CancellationToken.None);
        var request = Acknowledgement(command);
        await store.AcknowledgeCommandAsync(request, CancellationToken.None);

        // Act
        var result = await store.AcknowledgeCommandAsync(
            request with { Status = CommandAcknowledgementStatuses.Rejected },
            CancellationToken.None);

        // Assert
        Assert.Equal(ControllerCommandMutationStatus.Conflict, result.Status);
    }

    [Fact]
    public async Task GetAvailableCommandsAsync_WhenLeaseExpires_ThenReturnsCommandForRedelivery()
    {
        // Arrange
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new RunnerControlStore(NewRuntimeRoot(), timeProvider);
        var command = Command();
        await store.EnqueueCommandAsync(command, CancellationToken.None);
        var acknowledged = await store.AcknowledgeCommandAsync(
            Acknowledgement(command),
            CancellationToken.None);
        var claimed = Assert.IsType<ControllerCommand>(acknowledged.Command);
        var lease = Assert.IsType<CommandLease>(claimed.Execution.Lease);
        timeProvider.Advance(TimeSpan.FromSeconds(61));

        // Act
        var available = await store.GetAvailableCommandsAsync(
            "controller_1",
            CancellationToken.None);

        // Assert
        var redelivered = Assert.Single(available);
        Assert.Equal(command.CommandId, redelivered.CommandId);
        Assert.Equal(lease.LeaseId, redelivered.Execution.Lease?.LeaseId);
    }

    [Fact]
    public async Task AcknowledgeCommandAsync_WhenLeaseExpired_ThenCreatesNewDeliveryAttempt()
    {
        // Arrange
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new RunnerControlStore(NewRuntimeRoot(), timeProvider);
        var command = Command();
        await store.EnqueueCommandAsync(command, CancellationToken.None);
        var first = await store.AcknowledgeCommandAsync(
            Acknowledgement(command),
            CancellationToken.None);
        var firstCommand = Assert.IsType<ControllerCommand>(first.Command);
        var firstLease = Assert.IsType<CommandLease>(firstCommand.Execution.Lease);
        timeProvider.Advance(TimeSpan.FromSeconds(61));

        // Act
        var second = await store.AcknowledgeCommandAsync(
            Acknowledgement(command) with
            {
                AcknowledgementId = "ack_2",
                AcknowledgedAt = timeProvider.GetUtcNow()
            },
            CancellationToken.None);

        // Assert
        Assert.Equal(ControllerCommandMutationStatus.Ok, second.Status);
        var secondCommand = Assert.IsType<ControllerCommand>(second.Command);
        var secondLease = Assert.IsType<CommandLease>(secondCommand.Execution.Lease);
        Assert.NotEqual(firstLease.LeaseId, secondLease.LeaseId);
        Assert.Equal(2, secondLease.Attempt);
        Assert.Equal(2, secondLease.FencingToken);
        Assert.Equal(
            firstCommand.Correlation.AttemptId,
            secondCommand.Correlation.AttemptId);
        Assert.Equal("ack_2", second.Acknowledgement?.AcknowledgementId);
    }

    [Fact]
    public async Task CompleteCommandAsync_WhenFencingTokenMatches_ThenStopsRedelivery()
    {
        // Arrange
        var store = new RunnerControlStore(NewRuntimeRoot());
        var command = Command();
        await store.EnqueueCommandAsync(command, CancellationToken.None);
        var acknowledged = await store.AcknowledgeCommandAsync(
            Acknowledgement(command),
            CancellationToken.None);
        var claimed = Assert.IsType<ControllerCommand>(acknowledged.Command);
        var lease = Assert.IsType<CommandLease>(claimed.Execution.Lease);

        // Act
        var completed = await store.CompleteCommandAsync(
            Completion(claimed, lease.FencingToken),
            CancellationToken.None);
        var available = await store.GetAvailableCommandsAsync(
            "controller_1",
            CancellationToken.None);

        // Assert
        Assert.Equal(ControllerCommandMutationStatus.Ok, completed.Status);
        Assert.NotNull(completed.Completion);
        Assert.Empty(available);
    }

    [Fact]
    public async Task CompleteCommandAsync_WhenEquivalentRequestRepeats_ThenReplaysTerminalResult()
    {
        // Arrange
        var store = new RunnerControlStore(NewRuntimeRoot());
        var command = Command();
        await store.EnqueueCommandAsync(command, CancellationToken.None);
        var acknowledged = await store.AcknowledgeCommandAsync(
            Acknowledgement(command),
            CancellationToken.None);
        var claimed = Assert.IsType<ControllerCommand>(acknowledged.Command);
        var lease = Assert.IsType<CommandLease>(claimed.Execution.Lease);
        var firstRequest = Completion(claimed, lease.FencingToken);
        var first = await store.CompleteCommandAsync(
            firstRequest,
            CancellationToken.None);

        // Act
        var replayed = await store.CompleteCommandAsync(
            firstRequest with
            {
                CompletionId = "completion_2",
                CompletedAt = firstRequest.CompletedAt.AddMinutes(1)
            },
            CancellationToken.None);

        // Assert
        Assert.Equal(ControllerCommandMutationStatus.Ok, replayed.Status);
        Assert.Equal(
            first.Completion?.CompletionId,
            replayed.Completion?.CompletionId);
    }

    [Fact]
    public async Task CompleteCommandAsync_WhenDuplicateDiffers_ThenConflicts()
    {
        // Arrange
        var store = new RunnerControlStore(NewRuntimeRoot());
        var command = Command();
        await store.EnqueueCommandAsync(command, CancellationToken.None);
        var acknowledged = await store.AcknowledgeCommandAsync(
            Acknowledgement(command),
            CancellationToken.None);
        var claimed = Assert.IsType<ControllerCommand>(acknowledged.Command);
        var lease = Assert.IsType<CommandLease>(claimed.Execution.Lease);
        var completion = Completion(claimed, lease.FencingToken);
        await store.CompleteCommandAsync(completion, CancellationToken.None);

        // Act
        var result = await store.CompleteCommandAsync(
            completion with { DeliveryStatus = CommandDeliveryStatuses.Failed },
            CancellationToken.None);

        // Assert
        Assert.Equal(ControllerCommandMutationStatus.Conflict, result.Status);
    }

    [Fact]
    public async Task CompleteCommandAsync_WhenFencingTokenIsStale_ThenReturnsConflict()
    {
        // Arrange
        var store = new RunnerControlStore(NewRuntimeRoot());
        var command = Command();
        await store.EnqueueCommandAsync(command, CancellationToken.None);
        var acknowledged = await store.AcknowledgeCommandAsync(
            Acknowledgement(command),
            CancellationToken.None);
        var claimed = Assert.IsType<ControllerCommand>(acknowledged.Command);

        // Act
        var result = await store.CompleteCommandAsync(
            Completion(claimed, fencingToken: 0),
            CancellationToken.None);

        // Assert
        Assert.Equal(ControllerCommandMutationStatus.Conflict, result.Status);
    }

    [Fact]
    public async Task AddEventAsync_WhenEventIsCanonical_ThenPersistsEvent()
    {
        // Arrange
        var store = new RunnerControlStore(NewRuntimeRoot());
        var controllerEvent = Event();

        // Act
        await store.AddEventAsync(controllerEvent, CancellationToken.None);
        var events = await store.GetEventsAsync(CancellationToken.None);

        // Assert
        Assert.Equal(
            ControllerEventTypes.AgentSessionCreated,
            Assert.Single(events).EventType);
    }

    [Fact]
    public async Task SaveContentAsync_WhenDigestMatches_ThenReturnsContentReference()
    {
        // Arrange
        var store = new RunnerControlStore(NewRuntimeRoot());
        var bytes = "hello"u8.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var request = new ControllerContentUploadRequest(
            "text/plain",
            sha,
            bytes.Length,
            Convert.ToBase64String(bytes));

        // Act
        var response = await store.SaveContentAsync(request, CancellationToken.None);
        var contentId = response.ContentRef.Uri.Split('/').Last();
        var stored = await store.GetContentAsync(contentId, CancellationToken.None);

        // Assert
        Assert.StartsWith("tradecraft://content/", response.ContentRef.Uri);
        Assert.Equal(bytes, stored?.Bytes);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.SaveContentAsync(
                request with { Sha256 = "bad" },
                CancellationToken.None));
    }

    [Fact]
    public async Task Constructor_WhenV1StateExists_ThenReloadsNativeState()
    {
        // Arrange
        var root = NewRuntimeRoot();
        var store = new RunnerControlStore(root);
        var command = Command();
        var controllerEvent = Event();
        var heartbeat = Heartbeat();
        await store.EnqueueCommandAsync(command, CancellationToken.None);
        await store.AddEventAsync(controllerEvent, CancellationToken.None);
        await store.UpsertControllerHeartbeatAsync(
            heartbeat,
            CancellationToken.None);

        // Act
        var reloaded = new RunnerControlStore(root);

        // Assert
        Assert.Single(
            await reloaded.GetAvailableCommandsAsync(
                "controller_1",
                CancellationToken.None));
        Assert.Single(await reloaded.GetEventsAsync(CancellationToken.None));
        Assert.Equal(
            "online",
            (await reloaded.GetControllerHeartbeatAsync(
                "controller_1",
                CancellationToken.None))?.Status);
    }

    [Fact]
    public async Task Constructor_WhenCompletedIdempotentRequestExists_ThenReplaysTerminalResult()
    {
        // Arrange
        var root = NewRuntimeRoot();
        var store = new RunnerControlStore(root);
        var command = Command();
        await store.EnqueueCommandAsync(command, CancellationToken.None);
        var acknowledged = await store.AcknowledgeCommandAsync(
            Acknowledgement(command),
            CancellationToken.None);
        var claimed = Assert.IsType<ControllerCommand>(acknowledged.Command);
        var lease = Assert.IsType<CommandLease>(claimed.Execution.Lease);
        var completion = Completion(claimed, lease.FencingToken);
        await store.CompleteCommandAsync(completion, CancellationToken.None);

        // Act
        var reloaded = new RunnerControlStore(root);
        var replayed = await reloaded.EnqueueCommandAsync(
            EquivalentRetry(command, "cmd_2"),
            CancellationToken.None);

        // Assert
        Assert.Equal(ControllerCommandEnqueueStatus.Replayed, replayed.Status);
        Assert.Equal(command.CommandId, replayed.Command?.CommandId);
        Assert.Equal(completion.CompletionId, replayed.Completion?.CompletionId);
    }

    [Fact]
    public async Task Constructor_WhenCommandWriteWasInterrupted_ThenRecoversFromIdempotencyRecord()
    {
        // Arrange
        var root = NewRuntimeRoot();
        var store = new RunnerControlStore(root);
        var command = Command();
        await store.EnqueueCommandAsync(command, CancellationToken.None);
        File.Delete(
            Path.Combine(
                root,
                "controller-v1",
                "commands",
                $"{command.CommandId}.json"));

        // Act
        var reloaded = new RunnerControlStore(root);
        var replayed = await reloaded.EnqueueCommandAsync(
            EquivalentRetry(command, "cmd_2"),
            CancellationToken.None);

        // Assert
        Assert.Equal(ControllerCommandEnqueueStatus.Replayed, replayed.Status);
        Assert.Equal(command.CommandId, replayed.Command?.CommandId);
        Assert.True(
            File.Exists(
                Path.Combine(
                    root,
                    "controller-v1",
                    "commands",
                    $"{command.CommandId}.json")));
    }

    private static ControllerCommand Command()
    {
        return ControllerCommandFactory.Create(
            ControllerCommandTypes.CreateAgentSession,
            payloadRef: null,
            correlationId: "corr_1",
            idempotencyKey: "session_1",
            runnerId: "controller_1",
            agentId: "runtime_1",
            sessionId: "session_1",
            commandId: "cmd_1");
    }

    private static ControllerCommandAcknowledgement Acknowledgement(
        ControllerCommand command)
    {
        return new ControllerCommandAcknowledgement(
            ControllerMessageTypes.CommandAcknowledgement,
            ControllerProtocolVersions.Protocol,
            ControllerProtocolVersions.Schema,
            "ack_1",
            command.CommandId,
            "controller_1",
            CommandAcknowledgementStatuses.Accepted,
            DateTimeOffset.UtcNow,
            command.Correlation);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan duration)
        {
            _utcNow += duration;
        }
    }

    private static ControllerCommand EquivalentRetry(
        ControllerCommand command,
        string commandId)
    {
        var retryIssuedAt = command.IssuedAt.AddMinutes(1);
        var authorizationLifetime = command.AuthorizationContext.ExpiresAt
                                    - command.AuthorizationContext.IssuedAt;
        return command with
        {
            CommandId = commandId,
            IssuedAt = retryIssuedAt,
            Correlation = command.Correlation with { CommandId = commandId },
            AuthorizationContext = command.AuthorizationContext with
            {
                IssuedAt = retryIssuedAt,
                ExpiresAt = retryIssuedAt + authorizationLifetime
            },
            Execution = command.Execution with
            {
                Deadline = retryIssuedAt
                           + (command.Execution.Deadline - command.IssuedAt)
            },
            AvailableAt = retryIssuedAt
        };
    }

    private static ControllerCommandCompletion Completion(
        ControllerCommand command,
        long fencingToken)
    {
        return new ControllerCommandCompletion(
            ControllerMessageTypes.CommandCompletion,
            ControllerProtocolVersions.Protocol,
            ControllerProtocolVersions.Schema,
            "completion_1",
            command.CommandId,
            "controller_1",
            CommandDeliveryStatuses.Completed,
            DateTimeOffset.UtcNow,
            fencingToken,
            command.Correlation);
    }

    private static ControllerEvent Event()
    {
        return new ControllerEvent(
            ControllerMessageTypes.Event,
            ControllerProtocolVersions.Protocol,
            ControllerProtocolVersions.Schema,
            "event_1",
            "controller_1",
            ControllerEventTypes.AgentSessionCreated,
            new AggregateReference("agent_session", "session_1"),
            1,
            DateTimeOffset.UnixEpoch,
            ControllerProtocolVersions.Schema,
            new ResourceTarget(
                ControllerId: "controller_1",
                RuntimeId: "runtime_1",
                AgentSessionId: "session_1"),
            new ProtocolCorrelation(
                CommandId: "cmd_1",
                CorrelationId: "corr_1"),
            Payload: JsonSerializer.SerializeToElement(new { }));
    }

    private static ControllerHeartbeat Heartbeat()
    {
        return new ControllerHeartbeat(
            ControllerMessageTypes.Heartbeat,
            ControllerProtocolVersions.Protocol,
            ControllerProtocolVersions.Schema,
            "controller_1",
            "online",
            DateTimeOffset.UtcNow,
            [],
            new ControllerInventory(
                ImmutableArray<AgentRuntimeResource>.Empty));
    }

    private static string NewRuntimeRoot()
    {
        return Path.Combine(Path.GetTempPath(), Ids.New("controller_runtime"));
    }
}
