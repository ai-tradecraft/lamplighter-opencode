using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatThroughHarness.Protocol.V1;

/// <summary>Advertises a controller and its available runtime adapters.</summary>
/// <param name="MessageType">The message discriminator.</param>
/// <param name="ProtocolVersion">The wire-protocol version.</param>
/// <param name="SchemaVersion">The message schema version.</param>
/// <param name="RegistrationId">The registration attempt identifier.</param>
/// <param name="Controller">The controller descriptor.</param>
/// <param name="RegisteredAt">The registration time.</param>
/// <param name="AuthorizationContext">The optional registration authorization context.</param>
/// <param name="Extensions">The optional namespaced extension data.</param>
public sealed record ControllerRegistration(
    [property: JsonPropertyName("message_type")] string MessageType,
    [property: JsonPropertyName("protocol_version")] string ProtocolVersion,
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("registration_id")] string RegistrationId,
    [property: JsonPropertyName("controller")] ControllerDescriptor Controller,
    [property: JsonPropertyName("registered_at")] DateTimeOffset RegisteredAt,
    [property: JsonPropertyName("authorization_context")] AuthorizationContext? AuthorizationContext = null,
    [property: JsonPropertyName("extensions")] ImmutableDictionary<string, JsonElement>? Extensions = null);

/// <summary>Describes a Lamplighter Controller independently of its runtime providers.</summary>
/// <param name="ControllerId">The controller identifier.</param>
/// <param name="ControllerVersion">The controller software version.</param>
/// <param name="MachineName">The host machine name.</param>
/// <param name="SupportedProtocolVersions">The immutable supported protocol versions.</param>
/// <param name="TransportProfiles">The immutable outbound transport profiles.</param>
/// <param name="RuntimeAdapters">The immutable runtime-adapter summaries.</param>
/// <param name="Capacity">The controller capacity.</param>
/// <param name="Labels">The optional scheduling labels.</param>
public sealed record ControllerDescriptor(
    [property: JsonPropertyName("controller_id")] string ControllerId,
    [property: JsonPropertyName("controller_version")] string ControllerVersion,
    [property: JsonPropertyName("machine_name")] string MachineName,
    [property: JsonPropertyName("supported_protocol_versions")] ImmutableArray<string> SupportedProtocolVersions,
    [property: JsonPropertyName("transport_profiles")] ImmutableArray<string> TransportProfiles,
    [property: JsonPropertyName("runtime_adapters")] ImmutableArray<RuntimeAdapterSummary> RuntimeAdapters,
    [property: JsonPropertyName("capacity")] ControllerCapacity Capacity,
    [property: JsonPropertyName("labels")] ImmutableDictionary<string, string>? Labels = null);

/// <summary>Summarizes one runtime adapter available to a controller.</summary>
/// <param name="AdapterKind">The adapter kind.</param>
/// <param name="AdapterVersion">The adapter version.</param>
/// <param name="ProtocolVersions">The immutable adapter protocol versions.</param>
/// <param name="DeploymentModes">The immutable deployment modes.</param>
/// <param name="Capabilities">The immutable capability values.</param>
/// <param name="Health">The adapter health.</param>
public sealed record RuntimeAdapterSummary(
    [property: JsonPropertyName("adapter_kind")] string AdapterKind,
    [property: JsonPropertyName("adapter_version")] string AdapterVersion,
    [property: JsonPropertyName("protocol_versions")] ImmutableArray<string> ProtocolVersions,
    [property: JsonPropertyName("deployment_modes")] ImmutableArray<string> DeploymentModes,
    [property: JsonPropertyName("capabilities")] ImmutableDictionary<string, JsonElement> Capabilities,
    [property: JsonPropertyName("health")] string Health);

/// <summary>Describes controller scheduling capacity.</summary>
/// <param name="MaxRuntimes">The maximum managed runtimes.</param>
/// <param name="MaxConcurrentInvocations">The maximum concurrent invocations.</param>
public sealed record ControllerCapacity(
    [property: JsonPropertyName("max_runtimes")] int MaxRuntimes,
    [property: JsonPropertyName("max_concurrent_invocations")] int MaxConcurrentInvocations);

/// <summary>Accepts a controller registration and selects its command transport.</summary>
/// <param name="MessageType">The message discriminator.</param>
/// <param name="ProtocolVersion">The wire-protocol version.</param>
/// <param name="SchemaVersion">The message schema version.</param>
/// <param name="RegistrationId">The accepted registration identifier.</param>
/// <param name="ControllerId">The assigned controller identifier.</param>
/// <param name="AcceptedAt">The acceptance time.</param>
/// <param name="HeartbeatInterval">The ISO-8601 heartbeat interval.</param>
/// <param name="CommandChannel">The optional selected command channel.</param>
/// <param name="Extensions">The optional namespaced extension data.</param>
public sealed record ControllerRegistrationAccepted(
    [property: JsonPropertyName("message_type")] string MessageType,
    [property: JsonPropertyName("protocol_version")] string ProtocolVersion,
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("registration_id")] string RegistrationId,
    [property: JsonPropertyName("controller_id")] string ControllerId,
    [property: JsonPropertyName("accepted_at")] DateTimeOffset AcceptedAt,
    [property: JsonPropertyName("heartbeat_interval")] string HeartbeatInterval,
    [property: JsonPropertyName("command_channel")] ControllerCommandChannel? CommandChannel = null,
    [property: JsonPropertyName("extensions")] ImmutableDictionary<string, JsonElement>? Extensions = null);

/// <summary>Describes the outbound controller command channel.</summary>
/// <param name="Transport">The selected transport profile.</param>
/// <param name="Endpoint">The optional endpoint URI.</param>
public sealed record ControllerCommandChannel(
    [property: JsonPropertyName("transport")] string Transport,
    [property: JsonPropertyName("endpoint")] string? Endpoint = null);

/// <summary>Reports controller health, active commands, and resource inventory.</summary>
/// <param name="MessageType">The message discriminator.</param>
/// <param name="ProtocolVersion">The wire-protocol version.</param>
/// <param name="SchemaVersion">The message schema version.</param>
/// <param name="ControllerId">The controller identifier.</param>
/// <param name="Status">The controller status.</param>
/// <param name="ObservedAt">The observation time.</param>
/// <param name="ActiveCommandIds">The immutable active command identifiers.</param>
/// <param name="Inventory">The controller resource inventory.</param>
/// <param name="Extensions">The optional namespaced extension data.</param>
public sealed record ControllerHeartbeat(
    [property: JsonPropertyName("message_type")] string MessageType,
    [property: JsonPropertyName("protocol_version")] string ProtocolVersion,
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("controller_id")] string ControllerId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("observed_at")] DateTimeOffset ObservedAt,
    [property: JsonPropertyName("active_command_ids")] ImmutableArray<string> ActiveCommandIds,
    [property: JsonPropertyName("inventory")] ControllerInventory Inventory,
    [property: JsonPropertyName("extensions")] ImmutableDictionary<string, JsonElement>? Extensions = null);

/// <summary>Contains immutable runtime, session, and interaction inventory.</summary>
/// <param name="Runtimes">The immutable runtimes.</param>
/// <param name="Sessions">The immutable agent sessions.</param>
/// <param name="Interactions">The immutable interaction sessions.</param>
public sealed record ControllerInventory(
    [property: JsonPropertyName("runtimes")] ImmutableArray<AgentRuntimeResource> Runtimes,
    [property: JsonPropertyName("sessions")] ImmutableArray<AgentSessionResource>? Sessions = null,
    [property: JsonPropertyName("interactions")] ImmutableArray<InteractionSessionResource>? Interactions = null);

/// <summary>Describes one controller-managed agent runtime.</summary>
/// <param name="DocumentType">The resource document discriminator.</param>
/// <param name="RuntimeId">The runtime identifier.</param>
/// <param name="ControllerId">The owning controller identifier.</param>
/// <param name="Status">The runtime status.</param>
/// <param name="AdapterKind">The runtime adapter kind.</param>
/// <param name="DeploymentMode">The deployment mode.</param>
/// <param name="CreatedAt">The creation time.</param>
/// <param name="UpdatedAt">The last update time.</param>
/// <param name="AdapterVersion">The optional runtime adapter version.</param>
/// <param name="Capabilities">The optional immutable capability values.</param>
/// <param name="EndedAt">The optional terminal time.</param>
/// <param name="Extensions">The optional namespaced extension data.</param>
public sealed record AgentRuntimeResource(
    [property: JsonPropertyName("document_type")] string DocumentType,
    [property: JsonPropertyName("runtime_id")] string RuntimeId,
    [property: JsonPropertyName("controller_id")] string ControllerId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("adapter_kind")] string AdapterKind,
    [property: JsonPropertyName("deployment_mode")] string DeploymentMode,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("adapter_version")] string? AdapterVersion = null,
    [property: JsonPropertyName("capabilities")] ImmutableDictionary<string, JsonElement>? Capabilities = null,
    [property: JsonPropertyName("ended_at")] DateTimeOffset? EndedAt = null,
    [property: JsonPropertyName("extensions")] ImmutableDictionary<string, JsonElement>? Extensions = null);

/// <summary>Describes one persistent agent session.</summary>
/// <param name="DocumentType">The resource document discriminator.</param>
/// <param name="AgentSessionId">The agent session identifier.</param>
/// <param name="RuntimeId">The owning runtime identifier.</param>
/// <param name="Status">The session status.</param>
/// <param name="CreatedAt">The creation time.</param>
/// <param name="UpdatedAt">The last update time.</param>
/// <param name="ProviderSessionRef">The optional provider session reference.</param>
/// <param name="TranscriptAuthority">The optional transcript authority mode.</param>
/// <param name="EndedAt">The optional terminal time.</param>
/// <param name="Extensions">The optional namespaced extension data.</param>
public sealed record AgentSessionResource(
    [property: JsonPropertyName("document_type")] string DocumentType,
    [property: JsonPropertyName("agent_session_id")] string AgentSessionId,
    [property: JsonPropertyName("runtime_id")] string RuntimeId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("provider_session_ref")] string? ProviderSessionRef = null,
    [property: JsonPropertyName("transcript_authority")] string? TranscriptAuthority = null,
    [property: JsonPropertyName("ended_at")] DateTimeOffset? EndedAt = null,
    [property: JsonPropertyName("extensions")] ImmutableDictionary<string, JsonElement>? Extensions = null);

/// <summary>Describes a durable human-agent interaction session.</summary>
/// <param name="DocumentType">The resource document discriminator.</param>
/// <param name="InteractionSessionId">The interaction identifier.</param>
/// <param name="Target">The interaction target.</param>
/// <param name="Mode">The interaction mode.</param>
/// <param name="Purpose">The interaction purpose.</param>
/// <param name="Status">The interaction status.</param>
/// <param name="Participants">The immutable participants.</param>
/// <param name="CreatedAt">The creation time.</param>
/// <param name="UpdatedAt">The last update time.</param>
/// <param name="LastControllerSequence">The last acknowledged controller sequence.</param>
/// <param name="LastAgentSequence">The last acknowledged agent sequence.</param>
/// <param name="ClosedAt">The optional close time.</param>
/// <param name="TimeoutAt">The optional timeout.</param>
/// <param name="Extensions">The optional namespaced extension data.</param>
public sealed record InteractionSessionResource(
    [property: JsonPropertyName("document_type")] string DocumentType,
    [property: JsonPropertyName("interaction_session_id")] string InteractionSessionId,
    [property: JsonPropertyName("target")] ResourceTarget Target,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("participants")] ImmutableArray<InteractionParticipant> Participants,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("last_controller_sequence")] long? LastControllerSequence = null,
    [property: JsonPropertyName("last_agent_sequence")] long? LastAgentSequence = null,
    [property: JsonPropertyName("closed_at")] DateTimeOffset? ClosedAt = null,
    [property: JsonPropertyName("timeout_at")] DateTimeOffset? TimeoutAt = null,
    [property: JsonPropertyName("extensions")] ImmutableDictionary<string, JsonElement>? Extensions = null);

/// <summary>Identifies one interaction participant.</summary>
/// <param name="ParticipantRef">The participant URI.</param>
/// <param name="Role">The participant role.</param>
/// <param name="JoinedAt">The optional join time.</param>
/// <param name="LeftAt">The optional leave time.</param>
public sealed record InteractionParticipant(
    [property: JsonPropertyName("participant_ref")] string ParticipantRef,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("joined_at")] DateTimeOffset? JoinedAt = null,
    [property: JsonPropertyName("left_at")] DateTimeOffset? LeftAt = null);

/// <summary>Requests durable work from a Lamplighter Controller.</summary>
/// <param name="MessageType">The message discriminator.</param>
/// <param name="ProtocolVersion">The wire-protocol version.</param>
/// <param name="SchemaVersion">The message schema version.</param>
/// <param name="CommandId">The stable command identifier.</param>
/// <param name="CommandType">The canonical command kind.</param>
/// <param name="IdempotencyKey">The stable idempotency key.</param>
/// <param name="IssuedAt">The command issue time.</param>
/// <param name="Target">The target resources.</param>
/// <param name="Correlation">The workflow and trace correlation.</param>
/// <param name="AuthorizationContext">The immutable authorization context.</param>
/// <param name="Execution">The execution deadline and lease.</param>
/// <param name="AvailableAt">The optional first delivery time.</param>
/// <param name="PayloadRef">The optional claim-check payload.</param>
/// <param name="Payload">The optional inline payload.</param>
/// <param name="Extensions">The optional namespaced extension data.</param>
public sealed record ControllerCommand(
    [property: JsonPropertyName("message_type")] string MessageType,
    [property: JsonPropertyName("protocol_version")] string ProtocolVersion,
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("command_id")] string CommandId,
    [property: JsonPropertyName("command_type")] string CommandType,
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
    [property: JsonPropertyName("issued_at")] DateTimeOffset IssuedAt,
    [property: JsonPropertyName("target")] ResourceTarget Target,
    [property: JsonPropertyName("correlation")] ProtocolCorrelation Correlation,
    [property: JsonPropertyName("authorization_context")] AuthorizationContext AuthorizationContext,
    [property: JsonPropertyName("execution")] CommandExecution Execution,
    [property: JsonPropertyName("available_at")] DateTimeOffset? AvailableAt = null,
    [property: JsonPropertyName("payload_ref")] ContentReference? PayloadRef = null,
    [property: JsonPropertyName("payload")] JsonElement? Payload = null,
    [property: JsonPropertyName("extensions")] ImmutableDictionary<string, JsonElement>? Extensions = null);

/// <summary>Defines command deadline, heartbeat, and optional lease.</summary>
/// <param name="Deadline">The command deadline.</param>
/// <param name="HeartbeatInterval">The ISO-8601 heartbeat interval.</param>
/// <param name="Lease">The optional active lease.</param>
public sealed record CommandExecution(
    [property: JsonPropertyName("deadline")] DateTimeOffset Deadline,
    [property: JsonPropertyName("heartbeat_interval")] string HeartbeatInterval,
    [property: JsonPropertyName("lease")] CommandLease? Lease = null);

/// <summary>Leases one command to a controller with stale-writer fencing.</summary>
/// <param name="LeaseId">The lease identifier.</param>
/// <param name="ControllerId">The owning controller identifier.</param>
/// <param name="AcquiredAt">The acquisition time.</param>
/// <param name="ExpiresAt">The expiry time.</param>
/// <param name="Attempt">The delivery attempt.</param>
/// <param name="FencingToken">The monotonically increasing fencing token.</param>
public sealed record CommandLease(
    [property: JsonPropertyName("lease_id")] string LeaseId,
    [property: JsonPropertyName("controller_id")] string ControllerId,
    [property: JsonPropertyName("acquired_at")] DateTimeOffset AcquiredAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("attempt")] int Attempt,
    [property: JsonPropertyName("fencing_token")] long FencingToken);

/// <summary>Acknowledges controller receipt, acceptance, or rejection of a command.</summary>
/// <param name="MessageType">The message discriminator.</param>
/// <param name="ProtocolVersion">The wire-protocol version.</param>
/// <param name="SchemaVersion">The message schema version.</param>
/// <param name="AcknowledgementId">The acknowledgement identifier.</param>
/// <param name="CommandId">The command identifier.</param>
/// <param name="ControllerId">The controller identifier.</param>
/// <param name="Status">The acknowledgement status.</param>
/// <param name="AcknowledgedAt">The acknowledgement time.</param>
/// <param name="Correlation">The workflow and trace correlation.</param>
/// <param name="Lease">The optional acquired lease.</param>
/// <param name="Error">The optional rejection error.</param>
/// <param name="Extensions">The optional namespaced extension data.</param>
public sealed record ControllerCommandAcknowledgement(
    [property: JsonPropertyName("message_type")] string MessageType,
    [property: JsonPropertyName("protocol_version")] string ProtocolVersion,
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("acknowledgement_id")] string AcknowledgementId,
    [property: JsonPropertyName("command_id")] string CommandId,
    [property: JsonPropertyName("controller_id")] string ControllerId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("acknowledged_at")] DateTimeOffset AcknowledgedAt,
    [property: JsonPropertyName("correlation")] ProtocolCorrelation Correlation,
    [property: JsonPropertyName("lease")] CommandLease? Lease = null,
    [property: JsonPropertyName("error")] ProtocolError? Error = null,
    [property: JsonPropertyName("extensions")] ImmutableDictionary<string, JsonElement>? Extensions = null);

/// <summary>Requests bounded renewal of a command lease.</summary>
/// <param name="MessageType">The message discriminator.</param>
/// <param name="ProtocolVersion">The wire-protocol version.</param>
/// <param name="SchemaVersion">The message schema version.</param>
/// <param name="RenewalId">The renewal identifier.</param>
/// <param name="CommandId">The command identifier.</param>
/// <param name="ControllerId">The controller identifier.</param>
/// <param name="LeaseId">The lease identifier.</param>
/// <param name="FencingToken">The fencing token.</param>
/// <param name="RequestedExpiresAt">The requested expiry.</param>
/// <param name="RequestedAt">The renewal request time.</param>
/// <param name="Correlation">The workflow and trace correlation.</param>
/// <param name="Extensions">The optional namespaced extension data.</param>
public sealed record ControllerCommandLeaseRenewal(
    [property: JsonPropertyName("message_type")] string MessageType,
    [property: JsonPropertyName("protocol_version")] string ProtocolVersion,
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("renewal_id")] string RenewalId,
    [property: JsonPropertyName("command_id")] string CommandId,
    [property: JsonPropertyName("controller_id")] string ControllerId,
    [property: JsonPropertyName("lease_id")] string LeaseId,
    [property: JsonPropertyName("fencing_token")] long FencingToken,
    [property: JsonPropertyName("requested_expires_at")] DateTimeOffset RequestedExpiresAt,
    [property: JsonPropertyName("requested_at")] DateTimeOffset RequestedAt,
    [property: JsonPropertyName("correlation")] ProtocolCorrelation Correlation,
    [property: JsonPropertyName("extensions")] ImmutableDictionary<string, JsonElement>? Extensions = null);

/// <summary>Reports controller processing completion independently of an invocation outcome.</summary>
/// <param name="MessageType">The message discriminator.</param>
/// <param name="ProtocolVersion">The wire-protocol version.</param>
/// <param name="SchemaVersion">The message schema version.</param>
/// <param name="CompletionId">The completion identifier.</param>
/// <param name="CommandId">The command identifier.</param>
/// <param name="ControllerId">The controller identifier.</param>
/// <param name="DeliveryStatus">The command delivery terminal state.</param>
/// <param name="CompletedAt">The completion time.</param>
/// <param name="FencingToken">The fencing token.</param>
/// <param name="Correlation">The workflow and trace correlation.</param>
/// <param name="ResultRef">The optional command result content.</param>
/// <param name="Error">The optional delivery error.</param>
/// <param name="Extensions">The optional namespaced extension data.</param>
public sealed record ControllerCommandCompletion(
    [property: JsonPropertyName("message_type")] string MessageType,
    [property: JsonPropertyName("protocol_version")] string ProtocolVersion,
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("completion_id")] string CompletionId,
    [property: JsonPropertyName("command_id")] string CommandId,
    [property: JsonPropertyName("controller_id")] string ControllerId,
    [property: JsonPropertyName("delivery_status")] string DeliveryStatus,
    [property: JsonPropertyName("completed_at")] DateTimeOffset CompletedAt,
    [property: JsonPropertyName("fencing_token")] long FencingToken,
    [property: JsonPropertyName("correlation")] ProtocolCorrelation Correlation,
    [property: JsonPropertyName("result_ref")] ContentReference? ResultRef = null,
    [property: JsonPropertyName("error")] ProtocolError? Error = null,
    [property: JsonPropertyName("extensions")] ImmutableDictionary<string, JsonElement>? Extensions = null);

/// <summary>Reports a normalized, ordered controller event.</summary>
/// <param name="MessageType">The message discriminator.</param>
/// <param name="ProtocolVersion">The wire-protocol version.</param>
/// <param name="SchemaVersion">The message schema version.</param>
/// <param name="EventId">The stable event identifier.</param>
/// <param name="ControllerId">The controller identifier.</param>
/// <param name="EventType">The normalized event kind.</param>
/// <param name="Aggregate">The ordered event aggregate.</param>
/// <param name="Sequence">The monotonic aggregate sequence.</param>
/// <param name="OccurredAt">The event time.</param>
/// <param name="PayloadSchemaVersion">The payload schema version.</param>
/// <param name="Target">The related resources.</param>
/// <param name="Correlation">The workflow and trace correlation.</param>
/// <param name="PayloadRef">The optional claim-check payload.</param>
/// <param name="Payload">The optional inline payload.</param>
/// <param name="RawEventRef">The optional restricted provider event.</param>
/// <param name="Extensions">The optional namespaced extension data.</param>
public sealed record ControllerEvent(
    [property: JsonPropertyName("message_type")] string MessageType,
    [property: JsonPropertyName("protocol_version")] string ProtocolVersion,
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("event_id")] string EventId,
    [property: JsonPropertyName("controller_id")] string ControllerId,
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("aggregate")] AggregateReference Aggregate,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("payload_schema_version")] string PayloadSchemaVersion,
    [property: JsonPropertyName("target")] ResourceTarget Target,
    [property: JsonPropertyName("correlation")] ProtocolCorrelation Correlation,
    [property: JsonPropertyName("payload_ref")] ContentReference? PayloadRef = null,
    [property: JsonPropertyName("payload")] JsonElement? Payload = null,
    [property: JsonPropertyName("raw_event_ref")] ContentReference? RawEventRef = null,
    [property: JsonPropertyName("extensions")] ImmutableDictionary<string, JsonElement>? Extensions = null);
