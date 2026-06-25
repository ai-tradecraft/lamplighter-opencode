using ChatThroughHarness.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChatThroughHarness.Runner;

public sealed class RunnerWorker(
    IOptions<RunnerOptions> options,
    RunnerCommandLoop commandLoop,
    ILogger<RunnerWorker> logger) : BackgroundService
{
    private readonly RunnerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Lamplighter runner {RunnerId} starting for {OrchestratorBaseUri}.",
            _options.RunnerId,
            _options.OrchestratorBaseUri);

        var registration = new RunnerRegistrationRequest(
            RunnerId: _options.RunnerId,
            MachineName: Environment.MachineName,
            Version: typeof(RunnerWorker).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            Capabilities:
            [
                "commands.long_poll",
                "events.https_post",
                "content.claim_check",
                "opencode.serve"
            ],
            RegisteredAt: DateTimeOffset.UtcNow);

        logger.LogInformation(
            "Runner registration prepared with {CapabilityCount} capabilities.",
            registration.Capabilities.Count);

        await commandLoop.RunAsync(stoppingToken);
    }
}
