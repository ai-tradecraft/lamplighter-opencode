using System.Collections.Immutable;
using System.Text.Json;
using ChatThroughHarness.Protocol.V1;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChatThroughHarness.Runner;

public sealed class RunnerWorker(
    IOptions<RunnerOptions> options,
    IRunnerApiClient apiClient,
    IOpenCodeHealthProbe healthProbe,
    RunnerCommandLoop commandLoop,
    ILogger<RunnerWorker> logger) : BackgroundService
{
    private readonly RunnerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Lamplighter runner {RunnerId} starting for {OrchestratorBaseUri} with controller workspace {ControllerWorkspace}.",
            _options.RunnerId,
            _options.OrchestratorBaseUri,
            _options.ControllerWorkspace);

        var heartbeatTask = RunHeartbeatLoopAsync(stoppingToken);
        var commandTask = commandLoop.RunAsync(stoppingToken);
        await Task.WhenAll(heartbeatTask, commandTask);
    }

    private async Task RunHeartbeatLoopAsync(CancellationToken stoppingToken)
    {
        await PublishHeartbeatAsync(stoppingToken);
        using var timer = new PeriodicTimer(_options.HeartbeatInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await PublishHeartbeatAsync(stoppingToken);
        }
    }

    private async Task PublishHeartbeatAsync(CancellationToken cancellationToken)
    {
        var agents = await RunnerAgentInventory.ScanAsync(
            _options.ControllerWorkspace,
            DateTimeOffset.UtcNow,
            healthProbe,
            cancellationToken);
        var heartbeat = new ControllerHeartbeat(
            MessageType: ControllerMessageTypes.Heartbeat,
            ProtocolVersion: ControllerProtocolVersions.Protocol,
            SchemaVersion: ControllerProtocolVersions.Schema,
            ControllerId: _options.RunnerId,
            Status: "online",
            ObservedAt: DateTimeOffset.UtcNow,
            ActiveCommandIds: [],
            Inventory: new ControllerInventory(
                Runtimes: agents.Select(ToRuntimeResource).ToImmutableArray(),
                Sessions: agents
                    .SelectMany(ToSessionResources)
                    .ToImmutableArray()),
            Extensions: ImmutableDictionary<string, JsonElement>.Empty.Add(
                "tradecraft.adapter.capabilities",
                JsonSerializer.SerializeToElement(new
                {
                    adapter_kind = "opencode",
                    adapter_version = "0.1.0",
                    snapshot_capture = true,
                    snapshot_restore = true,
                    restoration_modes = new[] { "inspection" },
                    consistency_modes = new[] { "crash-consistent" },
                    transfer_profiles = new[] { "local-content-handle" }
                })));

        await apiClient.UpsertHeartbeatAsync(heartbeat, cancellationToken);
        logger.LogInformation(
            "Runner heartbeat published with {AgentCount} local agents.",
            agents.Count);
    }

    private AgentRuntimeResource ToRuntimeResource(RunnerAgentInventoryItem agent)
    {
        return new AgentRuntimeResource(
            DocumentType: "agent_runtime",
            RuntimeId: agent.AgentId ?? agent.AgentSessionId,
            ControllerId: _options.RunnerId,
            Status: agent.Status,
            AdapterKind: "opencode",
            DeploymentMode: "local_process",
            CreatedAt: agent.ObservedAt,
            UpdatedAt: agent.ObservedAt,
            Extensions: ImmutableDictionary<string, JsonElement>.Empty.Add(
                "tradecraft.poc",
                JsonSerializer.SerializeToElement(new
                {
                    agent_session_id = agent.AgentSessionId,
                    runtime_path = agent.RuntimePath,
                    workspace_path = agent.WorkspacePath,
                    opencode_endpoint = agent.OpenCodeEndpoint,
                    opencode_pid = agent.OpenCodePid
                })));
    }

    private static IEnumerable<AgentSessionResource> ToSessionResources(
        RunnerAgentInventoryItem agent)
    {
        return (agent.Sessions ?? [])
            .Select(session => new AgentSessionResource(
                DocumentType: "agent_session",
                AgentSessionId: session.SessionId,
                RuntimeId: agent.AgentId ?? agent.AgentSessionId,
                Status: session.Status,
                CreatedAt: session.ObservedAt,
                UpdatedAt: session.ObservedAt,
                ProviderSessionRef: session.OpenCodeSessionId,
                TranscriptAuthority: "provider",
                Extensions: ImmutableDictionary<string, JsonElement>.Empty.Add(
                    "tradecraft.poc",
                    JsonSerializer.SerializeToElement(new
                    {
                        runtime_path = session.RuntimePath
                    }))));
    }
}
