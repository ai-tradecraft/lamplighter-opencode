using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatThroughHarness.Protocol.V1;

/// <summary>References immutable content exchanged outside a protocol envelope.</summary>
/// <param name="Uri">The content URI.</param>
/// <param name="Sha256">The SHA-256 digest.</param>
/// <param name="ContentType">The media type.</param>
/// <param name="Length">The content length in bytes.</param>
/// <param name="SchemaRef">The optional schema URI.</param>
/// <param name="Encryption">The optional encryption mode.</param>
public sealed record ContentReference(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("content_type")] string ContentType,
    [property: JsonPropertyName("length")] long Length,
    [property: JsonPropertyName("schema_ref")] string? SchemaRef = null,
    [property: JsonPropertyName("encryption")] string? Encryption = null);

/// <summary>Correlates controller activity with workflow and distributed-trace resources.</summary>
/// <param name="CommandId">The originating command identifier.</param>
/// <param name="CausationId">The direct cause identifier.</param>
/// <param name="CorrelationId">The end-to-end correlation identifier.</param>
/// <param name="RunId">The workflow run identifier.</param>
/// <param name="BranchId">The workflow branch identifier.</param>
/// <param name="StepId">The logical step identifier.</param>
/// <param name="StepExecutionId">The step execution identifier.</param>
/// <param name="AttemptId">The workflow attempt identifier.</param>
/// <param name="InvocationId">The invocation identifier.</param>
/// <param name="TraceId">The W3C trace identifier.</param>
/// <param name="SpanId">The W3C span identifier.</param>
public sealed record ProtocolCorrelation(
    [property: JsonPropertyName("command_id")] string? CommandId = null,
    [property: JsonPropertyName("causation_id")] string? CausationId = null,
    [property: JsonPropertyName("correlation_id")] string? CorrelationId = null,
    [property: JsonPropertyName("run_id")] string? RunId = null,
    [property: JsonPropertyName("branch_id")] string? BranchId = null,
    [property: JsonPropertyName("step_id")] string? StepId = null,
    [property: JsonPropertyName("step_execution_id")] string? StepExecutionId = null,
    [property: JsonPropertyName("attempt_id")] string? AttemptId = null,
    [property: JsonPropertyName("invocation_id")] string? InvocationId = null,
    [property: JsonPropertyName("trace_id")] string? TraceId = null,
    [property: JsonPropertyName("span_id")] string? SpanId = null);

/// <summary>Identifies the protocol resources targeted by a command, event, or operation.</summary>
/// <param name="ControllerId">The controller identifier.</param>
/// <param name="RuntimeId">The agent runtime identifier.</param>
/// <param name="AgentSessionId">The persistent agent session identifier.</param>
/// <param name="InvocationId">The invocation identifier.</param>
/// <param name="InteractionSessionId">The interaction session identifier.</param>
/// <param name="CheckpointId">The checkpoint identifier.</param>
/// <param name="SnapshotId">The snapshot identifier.</param>
public sealed record ResourceTarget(
    [property: JsonPropertyName("controller_id")] string? ControllerId = null,
    [property: JsonPropertyName("runtime_id")] string? RuntimeId = null,
    [property: JsonPropertyName("agent_session_id")] string? AgentSessionId = null,
    [property: JsonPropertyName("invocation_id")] string? InvocationId = null,
    [property: JsonPropertyName("interaction_session_id")] string? InteractionSessionId = null,
    [property: JsonPropertyName("checkpoint_id")] string? CheckpointId = null,
    [property: JsonPropertyName("snapshot_id")] string? SnapshotId = null);

/// <summary>Captures the immutable authorization grant applied to protocol activity.</summary>
/// <param name="SubjectRef">The authorized subject URI.</param>
/// <param name="GrantRef">The authorization grant URI.</param>
/// <param name="IssuedAt">The grant issue time.</param>
/// <param name="ExpiresAt">The optional grant expiry time.</param>
/// <param name="TenantId">The optional tenant identifier.</param>
public sealed record AuthorizationContext(
    [property: JsonPropertyName("subject_ref")] string SubjectRef,
    [property: JsonPropertyName("grant_ref")] string GrantRef,
    [property: JsonPropertyName("issued_at")] DateTimeOffset IssuedAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt = null,
    [property: JsonPropertyName("tenant_id")] string? TenantId = null);

/// <summary>Identifies an event-sourced aggregate.</summary>
/// <param name="Type">The aggregate type.</param>
/// <param name="Id">The aggregate identifier.</param>
public sealed record AggregateReference(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] string Id);

/// <summary>Describes a durable artifact produced by an invocation or interaction.</summary>
/// <param name="ArtifactId">The artifact identifier.</param>
/// <param name="ArtifactType">The artifact type.</param>
/// <param name="ContentRef">The artifact content reference.</param>
/// <param name="CreatedAt">The artifact creation time.</param>
/// <param name="ProducerRef">The optional producer URI.</param>
/// <param name="RetentionPolicyRef">The optional retention policy URI.</param>
/// <param name="Sensitivity">The optional sensitivity classification.</param>
public sealed record ArtifactDescriptor(
    [property: JsonPropertyName("artifact_id")] string ArtifactId,
    [property: JsonPropertyName("artifact_type")] string ArtifactType,
    [property: JsonPropertyName("content_ref")] ContentReference ContentRef,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("producer_ref")] string? ProducerRef = null,
    [property: JsonPropertyName("retention_policy_ref")] string? RetentionPolicyRef = null,
    [property: JsonPropertyName("sensitivity")] string? Sensitivity = null);

/// <summary>Describes evidence supporting an invocation outcome.</summary>
/// <param name="Type">The evidence type.</param>
/// <param name="Status">The evidence status.</param>
/// <param name="Ref">The evidence URI.</param>
/// <param name="Summary">The optional evidence summary.</param>
public sealed record OutcomeEvidence(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("summary")] string? Summary = null);

/// <summary>Describes a blocker reported by an invocation.</summary>
/// <param name="Type">The blocker type.</param>
/// <param name="Summary">The blocker summary.</param>
/// <param name="QuestionRef">The optional question content reference.</param>
/// <param name="EvidenceRefs">The immutable supporting evidence URIs.</param>
public sealed record OutcomeBlocker(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("question_ref")] ContentReference? QuestionRef = null,
    [property: JsonPropertyName("evidence_refs")] ImmutableArray<string>? EvidenceRefs = null);

/// <summary>Describes a resumable continuation associated with an outcome.</summary>
/// <param name="CheckpointId">The optional checkpoint identifier.</param>
/// <param name="ResumeTokenRef">The optional resume-token URI.</param>
public sealed record OutcomeContinuation(
    [property: JsonPropertyName("checkpoint_id")] string? CheckpointId = null,
    [property: JsonPropertyName("resume_token_ref")] string? ResumeTokenRef = null);

/// <summary>Reports a classified protocol, controller, adapter, or provider error.</summary>
/// <param name="Code">The stable error code.</param>
/// <param name="Classification">The canonical error classification.</param>
/// <param name="Summary">The safe operator-facing summary.</param>
/// <param name="Retryable">Whether retry may be appropriate.</param>
/// <param name="TargetUsable">Whether the target remains usable.</param>
/// <param name="ReconciliationRequired">Whether reconciliation is required.</param>
/// <param name="DiagnosticRef">The optional restricted diagnostic content.</param>
/// <param name="ProviderRequestId">The optional provider request identifier.</param>
public sealed record ProtocolError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("classification")] string Classification,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("retryable")] bool Retryable,
    [property: JsonPropertyName("target_usable")] bool? TargetUsable = null,
    [property: JsonPropertyName("reconciliation_required")] bool? ReconciliationRequired = null,
    [property: JsonPropertyName("diagnostic_ref")] ContentReference? DiagnosticRef = null,
    [property: JsonPropertyName("provider_request_id")] string? ProviderRequestId = null);

/// <summary>Reports the structured, machine-routable outcome of an invocation.</summary>
/// <param name="Kind">The canonical outcome kind.</param>
/// <param name="Summary">The outcome summary.</param>
/// <param name="Artifacts">The immutable artifacts produced.</param>
/// <param name="Evidence">The immutable validation evidence.</param>
/// <param name="Blockers">The immutable blockers.</param>
/// <param name="MissingCapabilities">The immutable missing capabilities.</param>
/// <param name="Confidence">The optional confidence from zero to one.</param>
/// <param name="Resumable">Whether the invocation may be resumed.</param>
/// <param name="RecommendedAction">The optional recommended action.</param>
/// <param name="Continuation">The optional continuation descriptor.</param>
/// <param name="Error">The optional classified error.</param>
/// <param name="Extensions">The optional namespaced extension data.</param>
public sealed record InvocationOutcome(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("artifacts")] ImmutableArray<ArtifactDescriptor>? Artifacts = null,
    [property: JsonPropertyName("evidence")] ImmutableArray<OutcomeEvidence>? Evidence = null,
    [property: JsonPropertyName("blockers")] ImmutableArray<OutcomeBlocker>? Blockers = null,
    [property: JsonPropertyName("missing_capabilities")] ImmutableArray<string>? MissingCapabilities = null,
    [property: JsonPropertyName("confidence")] double? Confidence = null,
    [property: JsonPropertyName("resumable")] bool? Resumable = null,
    [property: JsonPropertyName("recommended_action")] string? RecommendedAction = null,
    [property: JsonPropertyName("continuation")] OutcomeContinuation? Continuation = null,
    [property: JsonPropertyName("error")] ProtocolError? Error = null,
    [property: JsonPropertyName("extensions")] ImmutableDictionary<string, JsonElement>? Extensions = null);
