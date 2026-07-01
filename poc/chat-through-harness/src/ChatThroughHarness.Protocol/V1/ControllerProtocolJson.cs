using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatThroughHarness.Protocol.V1;

/// <summary>Provides the JSON configuration for Agent Runtime controller protocol v1.</summary>
public static class ControllerProtocolJson
{
    /// <summary>Gets serializer options that preserve the canonical wire representation.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>Defines the protocol and document schema versions implemented by these contracts.</summary>
public static class ControllerProtocolVersions
{
    /// <summary>The controller wire-protocol version.</summary>
    public const string Protocol = "1.0";

    /// <summary>The controller message schema version.</summary>
    public const string Schema = "1.0";
}

/// <summary>Defines canonical top-level controller message discriminators.</summary>
public static class ControllerMessageTypes
{
    /// <summary>A controller registration message.</summary>
    public const string Registration = "controller.registration";

    /// <summary>An accepted controller registration message.</summary>
    public const string RegistrationAccepted = "controller.registration_accepted";

    /// <summary>A controller heartbeat message.</summary>
    public const string Heartbeat = "controller.heartbeat";

    /// <summary>An orchestrator-to-controller command.</summary>
    public const string Command = "controller.command";

    /// <summary>A controller command acknowledgement.</summary>
    public const string CommandAcknowledgement = "controller.command_acknowledgement";

    /// <summary>A controller command lease-renewal request.</summary>
    public const string CommandLeaseRenewal = "controller.command_lease_renewal";

    /// <summary>A controller command delivery completion.</summary>
    public const string CommandCompletion = "controller.command_completion";

    /// <summary>A normalized controller event.</summary>
    public const string Event = "controller.event";
}

/// <summary>Defines canonical controller command kinds.</summary>
public static class ControllerCommandTypes
{
    /// <summary>Prepares a durable agent runtime.</summary>
    public const string PrepareAgentRuntime = "PrepareAgentRuntime";

    /// <summary>Starts a prepared agent runtime.</summary>
    public const string StartAgentRuntime = "StartAgentRuntime";

    /// <summary>Stops an agent runtime gracefully.</summary>
    public const string StopAgentRuntime = "StopAgentRuntime";

    /// <summary>Prepares an immutable invocation.</summary>
    public const string PrepareInvocation = "PrepareInvocation";

    /// <summary>Starts an invocation.</summary>
    public const string StartInvocation = "StartInvocation";

    /// <summary>Sends additional input to an invocation.</summary>
    public const string SendInput = "SendInput";

    /// <summary>Requests an immutable invocation-state snapshot.</summary>
    public const string RequestSnapshot = "RequestSnapshot";

    /// <summary>Pauses an invocation.</summary>
    public const string PauseInvocation = "PauseInvocation";

    /// <summary>Resumes an invocation.</summary>
    public const string ResumeInvocation = "ResumeInvocation";

    /// <summary>Cancels an invocation cooperatively.</summary>
    public const string CancelInvocation = "CancelInvocation";

    /// <summary>Terminates an invocation forcibly.</summary>
    public const string TerminateInvocation = "TerminateInvocation";

    /// <summary>Restores a snapshot.</summary>
    public const string RestoreSnapshot = "RestoreSnapshot";

    /// <summary>Inspects an invocation.</summary>
    public const string InspectInvocation = "InspectInvocation";

    /// <summary>Collects invocation artifacts.</summary>
    public const string CollectArtifacts = "CollectArtifacts";

    /// <summary>Creates a persistent agent session.</summary>
    public const string CreateAgentSession = "CreateAgentSession";

    /// <summary>Closes a persistent agent session.</summary>
    public const string CloseAgentSession = "CloseAgentSession";

    /// <summary>Inspects a persistent agent session.</summary>
    public const string InspectAgentSession = "InspectAgentSession";

    /// <summary>Synchronizes an agent session transcript.</summary>
    public const string SynchronizeSessionHistory = "SynchronizeSessionHistory";

    /// <summary>Opens a durable human-agent interaction session.</summary>
    public const string OpenInteractionSession = "OpenInteractionSession";

    /// <summary>Sends ordered input to an interaction session.</summary>
    public const string SendInteractionInput = "SendInteractionInput";

    /// <summary>Acknowledges delivery of an interaction message.</summary>
    public const string AcknowledgeInteractionMessage = "AcknowledgeInteractionMessage";

    /// <summary>Closes an interaction session.</summary>
    public const string CloseInteractionSession = "CloseInteractionSession";
}

/// <summary>Defines canonical command acknowledgement states.</summary>
public static class CommandAcknowledgementStatuses
{
    /// <summary>The command was received.</summary>
    public const string Received = "received";

    /// <summary>The command was accepted for execution.</summary>
    public const string Accepted = "accepted";

    /// <summary>The command was rejected before execution.</summary>
    public const string Rejected = "rejected";
}

/// <summary>Defines canonical command delivery terminal states.</summary>
public static class CommandDeliveryStatuses
{
    /// <summary>The command was delivered and processed.</summary>
    public const string Completed = "completed";

    /// <summary>The controller failed to process the command.</summary>
    public const string Failed = "failed";

    /// <summary>The command was cancelled.</summary>
    public const string Cancelled = "cancelled";
}

/// <summary>Defines the controller event kinds used by the active POC.</summary>
public static class ControllerEventTypes
{
    /// <summary>An agent runtime is ready.</summary>
    public const string AgentRuntimeReady = "AgentRuntimeReady";

    /// <summary>An agent runtime stopped.</summary>
    public const string AgentRuntimeStopped = "AgentRuntimeStopped";

    /// <summary>An agent runtime was lost or failed.</summary>
    public const string AgentRuntimeLost = "AgentRuntimeLost";

    /// <summary>A persistent agent session was created.</summary>
    public const string AgentSessionCreated = "AgentSessionCreated";

    /// <summary>A persistent agent session was closed or failed.</summary>
    public const string AgentSessionClosed = "AgentSessionClosed";

    /// <summary>An agent session transcript was synchronized.</summary>
    public const string AgentSessionHistorySynchronized = "AgentSessionHistorySynchronized";

    /// <summary>An agent session transcript synchronization failed.</summary>
    public const string AgentSessionHistorySynchronizationFailed = "AgentSessionHistorySynchronizationFailed";

    /// <summary>Invocation progress was reported.</summary>
    public const string ProgressReported = "ProgressReported";

    /// <summary>An invocation outcome was reported.</summary>
    public const string OutcomeReported = "OutcomeReported";

    /// <summary>A human interaction was requested.</summary>
    public const string InteractionRequested = "InteractionRequested";

    /// <summary>An interaction session opened.</summary>
    public const string InteractionSessionOpened = "InteractionSessionOpened";

    /// <summary>An interaction message was received.</summary>
    public const string InteractionMessageReceived = "InteractionMessageReceived";

    /// <summary>An interaction session closed.</summary>
    public const string InteractionSessionClosed = "InteractionSessionClosed";
}

/// <summary>Defines stable POC error classifications within the v1 taxonomy.</summary>
public static class ProtocolErrorClassifications
{
    /// <summary>The controller failed while invoking its runtime adapter.</summary>
    public const string AdapterFailure = "internal_adapter_error";

    /// <summary>The command is not supported by the controller.</summary>
    public const string UnsupportedOperation = "unsupported_capability";

    /// <summary>The controller received invalid provider or adapter output.</summary>
    public const string InvalidResponse = "invalid_provider_response";
}
