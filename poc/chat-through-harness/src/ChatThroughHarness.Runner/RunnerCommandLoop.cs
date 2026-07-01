using ChatThroughHarness.Protocol.V1;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChatThroughHarness.Runner;

public sealed class RunnerCommandLoop(
    IRunnerApiClient apiClient,
    IRunnerCommandHandler commandHandler,
    IOptions<RunnerOptions> options,
    ILogger<RunnerCommandLoop> logger)
{
    private readonly RunnerOptions _options = options.Value;

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Runner command loop started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var commands = await apiClient.PollCommandsAsync(stoppingToken);
                if (commands.Count == 0)
                {
                    await Task.Delay(_options.PollInterval, stoppingToken);
                    continue;
                }

                foreach (var command in commands)
                {
                    await ProcessCommandAsync(command, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Runner command loop failed; backing off for {Backoff}.", _options.RetryBackoff);
                await Task.Delay(_options.RetryBackoff, stoppingToken);
            }
        }

        logger.LogInformation("Runner command loop stopped.");
    }

    private async Task ProcessCommandAsync(ControllerCommand command, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Acknowledging command {CommandId} of type {CommandType}.",
            command.CommandId,
            command.CommandType);
        var acknowledged = await apiClient.AcknowledgeCommandAsync(command, cancellationToken);

        RunnerCommandResult result;
        try
        {
            result = await commandHandler.HandleAsync(acknowledged, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Command {CommandId} failed in handler.",
                acknowledged.CommandId);
            result = RunnerCommandResult.Failed(new ProtocolError(
                Code: "controller_handler_failed",
                Classification: ProtocolErrorClassifications.AdapterFailure,
                Summary: ex.Message,
                Retryable: false));
        }

        await apiClient.CompleteCommandAsync(acknowledged, result, cancellationToken);
        logger.LogInformation(
            "Completed command {CommandId} with delivery status {DeliveryStatus}.",
            acknowledged.CommandId,
            result.DeliveryStatus);
    }
}
