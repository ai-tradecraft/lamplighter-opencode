namespace ChatThroughHarness.Api.Tests;

using System.Collections.Immutable;
using System.Text.Json;
using ChatThroughHarness.Protocol.V1;
using Xunit;

public sealed class ControllerProtocolV1Tests
{
    [Fact]
    public void Serialize_WhenCommandContainsWorkflowIdentity_ThenUsesCanonicalV1Names()
    {
        // Arrange
        var command = CreateCommand();

        // Act
        var json = JsonSerializer.Serialize(command, ControllerProtocolJson.Options);

        // Assert
        Assert.Contains("\"message_type\": \"controller.command\"", json);
        Assert.Contains("\"protocol_version\": \"1.0\"", json);
        Assert.Contains("\"command_id\": \"cmd_712\"", json);
        Assert.Contains("\"runtime_id\": \"runtime_123\"", json);
        Assert.Contains("\"agent_session_id\": \"session_456\"", json);
        Assert.Contains("\"invocation_id\": \"inv_913\"", json);
        Assert.Contains("\"fencing_token\": 19", json);
        Assert.Contains("\"authorization_context\"", json);
        Assert.DoesNotContain("\"payload\":", json);
        Assert.DoesNotContain("\"extensions\":", json);
    }

    [Fact]
    public void Deserialize_WhenRegistrationUsesCanonicalShape_ThenRoundTripsImmutableCollections()
    {
        // Arrange
        using var capabilityDocument = JsonDocument.Parse("true");
        var supportedVersionsSource = new List<string> { ControllerProtocolVersions.Protocol };
        var registration = new ControllerRegistration(
            MessageType: ControllerMessageTypes.Registration,
            ProtocolVersion: ControllerProtocolVersions.Protocol,
            SchemaVersion: ControllerProtocolVersions.Schema,
            RegistrationId: "registration_123",
            Controller: new ControllerDescriptor(
                ControllerId: "controller_local_1",
                ControllerVersion: "0.1.0",
                MachineName: "developer-mac",
                SupportedProtocolVersions: supportedVersionsSource.ToImmutableArray(),
                TransportProfiles: ["https_long_poll", "websocket"],
                RuntimeAdapters:
                [
                    new RuntimeAdapterSummary(
                        AdapterKind: "opencode",
                        AdapterVersion: "0.1.0",
                        ProtocolVersions: [ControllerProtocolVersions.Protocol],
                        DeploymentModes: ["local_process"],
                        Capabilities: ImmutableDictionary<string, JsonElement>.Empty.Add(
                            "persistent_sessions",
                            capabilityDocument.RootElement.Clone()),
                        Health: "healthy")
                ],
                Capacity: new ControllerCapacity(4, 2)),
            RegisteredAt: DateTimeOffset.Parse("2026-07-01T01:02:00Z"));
        supportedVersionsSource.Add("legacy");

        // Act
        var json = JsonSerializer.Serialize(registration, ControllerProtocolJson.Options);
        var roundTrip = JsonSerializer.Deserialize<ControllerRegistration>(
            json,
            ControllerProtocolJson.Options);

        // Assert
        Assert.NotNull(roundTrip);
        Assert.Single(roundTrip.Controller.SupportedProtocolVersions);
        Assert.Equal(ControllerProtocolVersions.Protocol, roundTrip.Controller.SupportedProtocolVersions[0]);
        Assert.Equal("opencode", Assert.Single(roundTrip.Controller.RuntimeAdapters).AdapterKind);
        Assert.DoesNotContain("legacy", roundTrip.Controller.SupportedProtocolVersions);
    }

    [Fact]
    public void Deserialize_WhenOutcomeIsBlocked_ThenPreservesStructuredContinuation()
    {
        // Arrange
        var outcome = new InvocationOutcome(
            Kind: "blocked",
            Summary: "A requirement is ambiguous.",
            Blockers:
            [
                new OutcomeBlocker(
                    Type: "ambiguous_requirement",
                    Summary: "Retention behavior is unspecified.")
            ],
            Confidence: 0.98,
            Resumable: true,
            RecommendedAction: "open_synchronous_interaction",
            Continuation: new OutcomeContinuation(
                CheckpointId: "checkpoint_182",
                ResumeTokenRef: "resume-token://inv_913/73a9"));

        // Act
        var json = JsonSerializer.Serialize(outcome, ControllerProtocolJson.Options);
        var roundTrip = JsonSerializer.Deserialize<InvocationOutcome>(
            json,
            ControllerProtocolJson.Options);

        // Assert
        Assert.NotNull(roundTrip);
        Assert.Equal("blocked", roundTrip.Kind);
        Assert.Equal("ambiguous_requirement", Assert.Single(roundTrip.Blockers ?? []).Type);
        Assert.Equal("checkpoint_182", roundTrip.Continuation?.CheckpointId);
        Assert.Equal("open_synchronous_interaction", roundTrip.RecommendedAction);
    }

    [Fact]
    public void Serialize_WhenLeaseRenewalIsRequested_ThenCarriesOwnerAndFencingToken()
    {
        // Arrange
        var renewal = new ControllerCommandLeaseRenewal(
            MessageType: ControllerMessageTypes.CommandLeaseRenewal,
            ProtocolVersion: ControllerProtocolVersions.Protocol,
            SchemaVersion: ControllerProtocolVersions.Schema,
            RenewalId: "renewal_20",
            CommandId: "cmd_712",
            ControllerId: "controller_local_1",
            LeaseId: "lease_19",
            FencingToken: 19,
            RequestedExpiresAt: DateTimeOffset.Parse("2026-07-01T01:13:00Z"),
            RequestedAt: DateTimeOffset.Parse("2026-07-01T01:07:30Z"),
            Correlation: new ProtocolCorrelation(
                CommandId: "cmd_712",
                CorrelationId: "corr_123",
                RunId: "run_123",
                InvocationId: "inv_913"));

        // Act
        var json = JsonSerializer.Serialize(renewal, ControllerProtocolJson.Options);

        // Assert
        Assert.Contains("\"message_type\": \"controller.command_lease_renewal\"", json);
        Assert.Contains("\"lease_id\": \"lease_19\"", json);
        Assert.Contains("\"controller_id\": \"controller_local_1\"", json);
        Assert.Contains("\"fencing_token\": 19", json);
    }

    [Fact]
    public void Serialize_WhenSynchronousInteractionIsOpen_ThenPreservesRealtimeSessionState()
    {
        // Arrange
        var interaction = new InteractionSessionResource(
            DocumentType: "interaction_session",
            InteractionSessionId: "interaction_1",
            Target: new ResourceTarget(
                ControllerId: "controller_local_1",
                RuntimeId: "runtime_123",
                AgentSessionId: "session_456",
                InvocationId: "inv_913",
                InteractionSessionId: "interaction_1"),
            Mode: "synchronous",
            Purpose: "clarification",
            Status: "active",
            Participants:
            [
                new InteractionParticipant(
                    "agent://runtime_123",
                    "agent",
                    DateTimeOffset.Parse("2026-07-01T01:12:00Z")),
                new InteractionParticipant(
                    "user://operator/42",
                    "human",
                    DateTimeOffset.Parse("2026-07-01T01:12:04Z"))
            ],
            CreatedAt: DateTimeOffset.Parse("2026-07-01T01:12:00Z"),
            UpdatedAt: DateTimeOffset.Parse("2026-07-01T01:12:04Z"),
            LastControllerSequence: 4,
            LastAgentSequence: 3);

        // Act
        var json = JsonSerializer.Serialize(
            interaction,
            ControllerProtocolJson.Options);
        var roundTrip = JsonSerializer.Deserialize<InteractionSessionResource>(
            json,
            ControllerProtocolJson.Options);

        // Assert
        Assert.Equal("synchronous", roundTrip?.Mode);
        Assert.Equal("active", roundTrip?.Status);
        Assert.Equal(2, roundTrip?.Participants.Length);
        Assert.Equal("interaction_1", roundTrip?.Target.InteractionSessionId);
        Assert.Equal(4, roundTrip?.LastControllerSequence);
    }

    private static ControllerCommand CreateCommand()
    {
        return new ControllerCommand(
            MessageType: ControllerMessageTypes.Command,
            ProtocolVersion: ControllerProtocolVersions.Protocol,
            SchemaVersion: ControllerProtocolVersions.Schema,
            CommandId: "cmd_712",
            CommandType: ControllerCommandTypes.StartInvocation,
            IdempotencyKey: "inv_913:start",
            IssuedAt: DateTimeOffset.Parse("2026-07-01T01:03:00Z"),
            Target: new ResourceTarget(
                ControllerId: "controller_local_1",
                RuntimeId: "runtime_123",
                AgentSessionId: "session_456",
                InvocationId: "inv_913"),
            Correlation: new ProtocolCorrelation(
                CommandId: "cmd_712",
                CorrelationId: "corr_123",
                RunId: "run_123",
                BranchId: "main",
                StepId: "implement",
                StepExecutionId: "step_execution_7",
                AttemptId: "attempt_3",
                InvocationId: "inv_913",
                TraceId: "0123456789abcdef0123456789abcdef",
                SpanId: "0123456789abcdef"),
            AuthorizationContext: new AuthorizationContext(
                SubjectRef: "user://operator/42",
                GrantRef: "authorization-grant://invocation/inv_913",
                IssuedAt: DateTimeOffset.Parse("2026-07-01T01:02:00Z"),
                ExpiresAt: DateTimeOffset.Parse("2026-07-01T03:02:00Z")),
            Execution: new CommandExecution(
                Deadline: DateTimeOffset.Parse("2026-07-01T02:00:00Z"),
                HeartbeatInterval: "PT30S",
                Lease: new CommandLease(
                    LeaseId: "lease_19",
                    ControllerId: "controller_local_1",
                    AcquiredAt: DateTimeOffset.Parse("2026-07-01T01:03:00Z"),
                    ExpiresAt: DateTimeOffset.Parse("2026-07-01T01:08:00Z"),
                    Attempt: 1,
                    FencingToken: 19)),
            AvailableAt: DateTimeOffset.Parse("2026-07-01T01:03:00Z"),
            PayloadRef: new ContentReference(
                Uri: "content://invocations/inv_913/input",
                Sha256: new string('3', 64),
                ContentType: "application/vnd.tradecraft.invocation-input+json",
                Length: 842));
    }
}
