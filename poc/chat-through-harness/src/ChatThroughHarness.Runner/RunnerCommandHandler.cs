using ChatThroughHarness.Protocol;
using Microsoft.Extensions.Logging;

namespace ChatThroughHarness.Runner;

public interface IRunnerCommandHandler
{
    Task<RunnerCommandResult> HandleAsync(RunnerCommandEnvelope command, CancellationToken cancellationToken);
}

public sealed record RunnerCommandResult(
    string Status,
    ClaimCheckContentRef? ResultRef,
    RunnerFailure? Failure)
{
    public static RunnerCommandResult Completed(ClaimCheckContentRef? resultRef = null)
    {
        return new RunnerCommandResult(RunnerCommandStatuses.Completed, resultRef, null);
    }

    public static RunnerCommandResult Failed(RunnerFailure failure)
    {
        return new RunnerCommandResult(RunnerCommandStatuses.Failed, null, failure);
    }
}

public sealed class DeferredRunnerCommandHandler(ILogger<DeferredRunnerCommandHandler> logger) : IRunnerCommandHandler
{
    public Task<RunnerCommandResult> HandleAsync(RunnerCommandEnvelope command, CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "Command {CommandId} of type {CommandType} was claimed before the harness command handler was implemented.",
            command.Id,
            command.Type);

        return Task.FromResult(RunnerCommandResult.Failed(new RunnerFailure(
            Summary: "Runner command handler is not implemented yet.",
            DetailRef: null,
            Retryable: true)));
    }
}
