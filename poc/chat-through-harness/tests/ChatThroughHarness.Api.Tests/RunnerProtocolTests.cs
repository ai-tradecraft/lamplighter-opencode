namespace ChatThroughHarness.Api.Tests;

using System.Text.Json;
using ChatThroughHarness.Protocol;
using Xunit;

public sealed class RunnerProtocolTests
{
    [Fact]
    public void VersionTwoEnvelopeCarriesAgentAndSessionIdentity()
    {
        var now = DateTimeOffset.UtcNow;
        var command = new RunnerCommandEnvelope(
            Id: "cmd_1",
            RunnerId: "runner_1",
            AgentSessionId: "session_1",
            Type: RunnerCommandTypes.SubmitAgentTurn,
            Status: RunnerCommandStatuses.Pending,
            PayloadRef: null,
            CorrelationId: "turn_1",
            IdempotencyKey: "turn_1",
            CreatedAt: now,
            AvailableAt: now,
            Lease: null,
            AgentId: "agent_1",
            SessionId: "session_1");

        var json = JsonSerializer.Serialize(command, RunnerProtocolJson.Options);

        Assert.Contains("\"agent_id\": \"agent_1\"", json);
        Assert.Contains("\"session_id\": \"session_1\"", json);
    }

    [Fact]
    public void VersionTwoHeartbeatCarriesChildSessions()
    {
        var observedAt = DateTimeOffset.UtcNow;
        var heartbeat = new RunnerHeartbeat(
            RunnerId: "runner_1",
            Status: "online",
            ActiveCommandIds: [],
            ObservedAt: observedAt,
            Agents:
            [
                new RunnerAgentInventoryItem(
                    AgentSessionId: "session_legacy",
                    Status: "ready",
                    RuntimePath: "/runtime/agent_1",
                    WorkspacePath: "/workspace/agent_1",
                    OpenCodeEndpoint: "http://127.0.0.1:4096",
                    OpenCodePid: 123,
                    ObservedAt: observedAt,
                    AgentId: "agent_1",
                    Sessions:
                    [
                        new RunnerAgentSessionInventoryItem(
                            "session_1",
                            "ready",
                            "/runtime/agent_1/sessions/session_1",
                            "opencode_1",
                            observedAt)
                    ])
            ]);

        var json = JsonSerializer.Serialize(heartbeat, RunnerProtocolJson.Options);

        Assert.Contains("\"protocol_version\": 2", json);
        Assert.Contains("\"agent_id\": \"agent_1\"", json);
        Assert.Contains("\"session_id\": \"session_1\"", json);
    }

    [Fact]
    public void CommandEnvelopeSerializesAsSnakeCaseContract()
    {
        var contentRef = new ClaimCheckContentRef(
            Uri: "tradecraft://content/prompt_1",
            Sha256: "abc123",
            ContentType: "application/json",
            Length: 42);
        var command = new RunnerCommandEnvelope(
            Id: "cmd_1",
            RunnerId: null,
            AgentSessionId: "session_1",
            Type: RunnerCommandTypes.SubmitAgentTurn,
            Status: RunnerCommandStatuses.Pending,
            PayloadRef: contentRef,
            CorrelationId: "corr_1",
            IdempotencyKey: "turn_1",
            CreatedAt: DateTimeOffset.UnixEpoch,
            AvailableAt: DateTimeOffset.UnixEpoch,
            Lease: null);

        var json = JsonSerializer.Serialize(command, RunnerProtocolJson.Options);

        Assert.Contains("\"agent_session_id\"", json);
        Assert.Contains("\"payload_ref\"", json);
        Assert.Contains("\"idempotency_key\"", json);
        Assert.DoesNotContain("runner_id", json);
        Assert.Contains(RunnerCommandTypes.SubmitAgentTurn, json);
    }

    [Fact]
    public void LeaseCompletionCarriesFailureOrResultByClaimCheck()
    {
        var failureRef = new ClaimCheckContentRef(
            Uri: "tradecraft://content/failure_1",
            Sha256: "def456",
            ContentType: "text/plain",
            Length: 128);
        var completion = new CompleteRunnerCommandRequest(
            RunnerId: "runner_1",
            LeaseId: "lease_1",
            Status: RunnerCommandStatuses.Failed,
            CompletedAt: DateTimeOffset.UnixEpoch,
            ResultRef: null,
            Failure: new RunnerFailure("OpenCode failed.", failureRef, Retryable: false));

        var json = JsonSerializer.Serialize(completion, RunnerProtocolJson.Options);
        var roundTrip = JsonSerializer.Deserialize<CompleteRunnerCommandRequest>(json, RunnerProtocolJson.Options);

        Assert.NotNull(roundTrip);
        Assert.Equal("runner_1", roundTrip.RunnerId);
        Assert.Equal(RunnerCommandStatuses.Failed, roundTrip.Status);
        Assert.Equal("tradecraft://content/failure_1", roundTrip.Failure?.DetailRef?.Uri);
        Assert.DoesNotContain("result_ref", json);
    }

    [Fact]
    public void ClarificationPayloadSupportsAsyncQuestionnaire()
    {
        var payload = new ClarificationRequestPayload(
            Id: "clarification_1",
            AgentSessionId: "session_1",
            AgentId: "opencode.default",
            Mode: ClarificationModes.Async,
            Status: ClarificationStatuses.Requested,
            Title: "Deployment preferences",
            PromptRef: null,
            Questions:
            [
                new ClarificationQuestion(
                    Id: "target_environment",
                    Type: "single_choice",
                    Prompt: "Where should this be deployed?",
                    Required: true,
                    Options:
                    [
                        new ClarificationOption("azure", "Azure"),
                        new ClarificationOption("local", "Local only")
                    ])
            ],
            ChatSessionId: null,
            DueAt: null);

        var json = JsonSerializer.Serialize(payload, RunnerProtocolJson.Options);

        Assert.Contains("\"mode\": \"async\"", json);
        Assert.Contains("\"questions\"", json);
        Assert.Contains("\"target_environment\"", json);
        Assert.DoesNotContain("chat_session_id", json);
    }
}
