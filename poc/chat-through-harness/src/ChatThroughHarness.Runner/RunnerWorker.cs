using ChatThroughHarness.Protocol;
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
        var heartbeat = new RunnerHeartbeat(
            RunnerId: _options.RunnerId,
            Status: "online",
            ActiveCommandIds: [],
            ObservedAt: DateTimeOffset.UtcNow,
            Agents: agents);

        await apiClient.UpsertHeartbeatAsync(heartbeat, cancellationToken);
        logger.LogInformation(
            "Runner heartbeat published with {AgentCount} local agents.",
            agents.Count);
    }
}
