using ChatThroughHarness.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChatThroughHarness.Runner;

public sealed class RunnerCommandLoop(
    RunnerApiClient apiClient,
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

    private async Task ProcessCommandAsync(RunnerCommandEnvelope command, CancellationToken cancellationToken)
    {
        logger.LogInformation("Claiming command {CommandId} of type {CommandType}.", command.Id, command.Type);
        var claimed = await apiClient.ClaimCommandAsync(command, cancellationToken);

        RunnerCommandResult result;
        try
        {
            result = await commandHandler.HandleAsync(claimed, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Command {CommandId} failed in handler.", claimed.Id);
            result = RunnerCommandResult.Failed(new RunnerFailure(
                Summary: ex.Message,
                DetailRef: null,
                Retryable: false));
        }

        await apiClient.CompleteCommandAsync(claimed, result, cancellationToken);
        logger.LogInformation("Completed command {CommandId} with status {Status}.", claimed.Id, result.Status);
    }
}
