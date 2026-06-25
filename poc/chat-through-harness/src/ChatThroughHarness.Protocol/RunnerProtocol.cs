using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatThroughHarness.Protocol;

public static class RunnerCommandTypes
{
    public const string PrepareAgentSession = "agent_session.prepare";
    public const string SubmitAgentTurn = "agent_turn.submit";
    public const string CancelAgentSession = "agent_session.cancel";
    public const string RespondToClarification = "agent_clarification.respond";
    public const string SendChatMessage = "agent_chat.message";
}

public static class RunnerEventTypes
{
    public const string RunnerRegistered = "runner.registered";
    public const string AgentSessionReady = "agent_session.ready";
    public const string AgentSessionFailed = "agent_session.failed";
    public const string AgentTurnOutputDelta = "agent_turn.output_delta";
    public const string AgentTurnCompleted = "agent_turn.completed";
    public const string AgentTurnFailed = "agent_turn.failed";
    public const string ClarificationRequested = "agent_clarification.requested";
    public const string AgentChatStarted = "agent_chat.started";
    public const string AgentChatMessage = "agent_chat.message";
    public const string AgentChatEnded = "agent_chat.ended";
}

public static class RunnerCommandStatuses
{
    public const string Pending = "pending";
    public const string Claimed = "claimed";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Expired = "expired";
}

public static class ClarificationModes
{
    public const string Async = "async";
    public const string Sync = "sync";
}

public static class ClarificationStatuses
{
    public const string Requested = "requested";
    public const string Active = "active";
    public const string Answered = "answered";
    public const string Cancelled = "cancelled";
    public const string Expired = "expired";
}

public static class RunnerProtocolJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed record RunnerRegistrationRequest(
    [property: JsonPropertyName("runner_id")] string? RunnerId,
    [property: JsonPropertyName("machine_name")] string MachineName,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities,
    [property: JsonPropertyName("registered_at")] DateTimeOffset RegisteredAt);

public sealed record RunnerRegistrationResponse(
    [property: JsonPropertyName("runner_id")] string RunnerId,
    [property: JsonPropertyName("access_token_hint")] string? AccessTokenHint,
    [property: JsonPropertyName("command_poll_path")] string CommandPollPath,
    [property: JsonPropertyName("heartbeat_interval_seconds")] int HeartbeatIntervalSeconds);

public sealed record RunnerHeartbeat(
    [property: JsonPropertyName("runner_id")] string RunnerId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("active_command_ids")] IReadOnlyList<string> ActiveCommandIds,
    [property: JsonPropertyName("observed_at")] DateTimeOffset ObservedAt);

public sealed record RunnerCommandEnvelope(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("runner_id")] string? RunnerId,
    [property: JsonPropertyName("agent_session_id")] string AgentSessionId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("payload_ref")] ClaimCheckContentRef? PayloadRef,
    [property: JsonPropertyName("correlation_id")] string CorrelationId,
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("available_at")] DateTimeOffset AvailableAt,
    [property: JsonPropertyName("lease")] RunnerCommandLease? Lease);

public sealed record RunnerCommandLease(
    [property: JsonPropertyName("lease_id")] string LeaseId,
    [property: JsonPropertyName("runner_id")] string RunnerId,
    [property: JsonPropertyName("claimed_at")] DateTimeOffset ClaimedAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("attempt")] int Attempt);

public sealed record ClaimRunnerCommandRequest(
    [property: JsonPropertyName("runner_id")] string RunnerId,
    [property: JsonPropertyName("lease_seconds")] int LeaseSeconds);

public sealed record CompleteRunnerCommandRequest(
    [property: JsonPropertyName("runner_id")] string RunnerId,
    [property: JsonPropertyName("lease_id")] string LeaseId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("completed_at")] DateTimeOffset CompletedAt,
    [property: JsonPropertyName("result_ref")] ClaimCheckContentRef? ResultRef,
    [property: JsonPropertyName("failure")] RunnerFailure? Failure);

public sealed record RunnerFailure(
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("detail_ref")] ClaimCheckContentRef? DetailRef,
    [property: JsonPropertyName("retryable")] bool Retryable);

public sealed record RunnerEventEnvelope(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("runner_id")] string RunnerId,
    [property: JsonPropertyName("agent_session_id")] string AgentSessionId,
    [property: JsonPropertyName("command_id")] string? CommandId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("payload_ref")] ClaimCheckContentRef? PayloadRef,
    [property: JsonPropertyName("causation_id")] string? CausationId,
    [property: JsonPropertyName("correlation_id")] string CorrelationId,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

public sealed record ClaimCheckContentRef(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("content_type")] string ContentType,
    [property: JsonPropertyName("length")] long Length);

public sealed record ClaimCheckContentUploadRequest(
    [property: JsonPropertyName("content_type")] string ContentType,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("length")] long Length,
    [property: JsonPropertyName("content_base64")] string ContentBase64);

public sealed record ClaimCheckContentUploadResponse(
    [property: JsonPropertyName("content_ref")] ClaimCheckContentRef ContentRef);

public sealed record ClarificationRequestPayload(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("agent_session_id")] string AgentSessionId,
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("prompt_ref")] ClaimCheckContentRef? PromptRef,
    [property: JsonPropertyName("questions")] IReadOnlyList<ClarificationQuestion> Questions,
    [property: JsonPropertyName("chat_session_id")] string? ChatSessionId,
    [property: JsonPropertyName("due_at")] DateTimeOffset? DueAt);

public sealed record ClarificationQuestion(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("required")] bool Required,
    [property: JsonPropertyName("options")] IReadOnlyList<ClarificationOption> Options);

public sealed record ClarificationOption(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("label")] string Label);

public sealed record ClarificationResponsePayload(
    [property: JsonPropertyName("clarification_id")] string ClarificationId,
    [property: JsonPropertyName("agent_session_id")] string AgentSessionId,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("answers")] IReadOnlyList<ClarificationAnswer> Answers,
    [property: JsonPropertyName("message_ref")] ClaimCheckContentRef? MessageRef,
    [property: JsonPropertyName("responded_at")] DateTimeOffset RespondedAt);

public sealed record ClarificationAnswer(
    [property: JsonPropertyName("question_id")] string QuestionId,
    [property: JsonPropertyName("value")] string Value);
