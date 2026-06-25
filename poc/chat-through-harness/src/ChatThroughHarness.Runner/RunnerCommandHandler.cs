using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ChatThroughHarness.Protocol;

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

public sealed class CliRunnerCommandHandler(
    IRunnerApiClient apiClient,
    IHarnessProcessRunner processRunner,
    IOptions<RunnerOptions> options,
    ILogger<CliRunnerCommandHandler> logger) : IRunnerCommandHandler
{
    private readonly RunnerOptions _options = options.Value;

    public async Task<RunnerCommandResult> HandleAsync(RunnerCommandEnvelope command, CancellationToken cancellationToken)
    {
        return command.Type switch
        {
            RunnerCommandTypes.PrepareAgentSession => await PrepareSessionAsync(command, cancellationToken),
            RunnerCommandTypes.SubmitAgentTurn => await SubmitTurnAsync(command, cancellationToken),
            RunnerCommandTypes.CancelAgentSession => await CancelSessionAsync(command, cancellationToken),
            _ => RunnerCommandResult.Failed(new RunnerFailure($"Unsupported command type {command.Type}.", null, Retryable: false))
        };
    }

    private async Task<RunnerCommandResult> PrepareSessionAsync(
        RunnerCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        var payload = await DownloadPayloadAsync(command, cancellationToken);
        var sessionRoot = SessionRoot(command.AgentSessionId);
        Directory.CreateDirectory(sessionRoot);
        var specPath = Path.Combine(sessionRoot, "agent-session-spec.json");
        await File.WriteAllTextAsync(specPath, payload, cancellationToken);

        var prepareOutput = await processRunner.RunAsync(
            "uv",
            ["run", "lamplighter-opencode", "prepare-session", "--spec", specPath, "--runtime-root", _options.RuntimeRoot, "--json"],
            cancellationToken);

        if (prepareOutput.ExitCode != 0)
        {
            return await CompleteFromProcessAsync(
                command,
                prepareOutput,
                successEventType: RunnerEventTypes.AgentSessionReady,
                failureEventType: RunnerEventTypes.AgentSessionFailed,
                successContentType: "application/vnd.tradecraft.prepare-session-result+json",
                cancellationToken);
        }

        var startOutput = await processRunner.RunAsync(
            "uv",
            ["run", "lamplighter-opencode", "start-session", "--session", command.AgentSessionId, "--runtime-root", _options.RuntimeRoot, "--json"],
            cancellationToken);

        return await CompleteFromProcessAsync(
            command,
            startOutput,
            successEventType: RunnerEventTypes.AgentSessionReady,
            failureEventType: RunnerEventTypes.AgentSessionFailed,
            successContentType: "application/vnd.tradecraft.start-session-result+json",
            cancellationToken);
    }

    private async Task<RunnerCommandResult> SubmitTurnAsync(
        RunnerCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        var payload = await DownloadPayloadAsync(command, cancellationToken);
        var sessionRoot = SessionRoot(command.AgentSessionId);
        Directory.CreateDirectory(sessionRoot);
        var requestPath = Path.Combine(sessionRoot, $"{command.CorrelationId}.request.json");
        await File.WriteAllTextAsync(requestPath, payload, cancellationToken);

        var output = await processRunner.RunAsync(
            "uv",
            ["run", "lamplighter-opencode", "submit-turn", "--session", command.AgentSessionId, "--request", requestPath, "--json"],
            cancellationToken);

        return await CompleteFromProcessAsync(
            command,
            output,
            successEventType: RunnerEventTypes.AgentTurnCompleted,
            failureEventType: RunnerEventTypes.AgentTurnFailed,
            successContentType: "application/vnd.tradecraft.agent-turn-result+json",
            cancellationToken);
    }

    private async Task<RunnerCommandResult> CancelSessionAsync(
        RunnerCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Received cancel command {CommandId} for session {SessionId}.", command.Id, command.AgentSessionId);
        var payloadRef = command.PayloadRef is null
            ? null
            : await apiClient.UploadContentAsync(
                await apiClient.DownloadContentStringAsync(command.PayloadRef, cancellationToken),
                command.PayloadRef.ContentType,
                cancellationToken);
        await PublishEventAsync(command, "agent_session.cancelled", payloadRef, cancellationToken);
        return RunnerCommandResult.Completed(payloadRef);
    }

    private async Task<RunnerCommandResult> CompleteFromProcessAsync(
        RunnerCommandEnvelope command,
        ProcessOutput output,
        string successEventType,
        string failureEventType,
        string successContentType,
        CancellationToken cancellationToken)
    {
        if (output.ExitCode == 0)
        {
            var resultRef = await apiClient.UploadContentAsync(output.Stdout, successContentType, cancellationToken);
            await PublishEventAsync(command, successEventType, resultRef, cancellationToken);
            return RunnerCommandResult.Completed(resultRef);
        }

        var diagnostics = $"""
            command: {output.Command}
            exit_code: {output.ExitCode}

            stdout:
            {output.Stdout}

            stderr:
            {output.Stderr}
            """;
        var failureRef = await apiClient.UploadContentAsync(
            diagnostics,
            "text/plain",
            cancellationToken);
        var failure = new RunnerFailure(
            Summary: $"Harness command failed with exit code {output.ExitCode}.",
            DetailRef: failureRef,
            Retryable: false);
        await PublishEventAsync(command, failureEventType, failureRef, cancellationToken);
        return RunnerCommandResult.Failed(failure);
    }

    private async Task<string> DownloadPayloadAsync(
        RunnerCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        if (command.PayloadRef is null)
        {
            throw new InvalidOperationException($"Command {command.Id} does not have a payload_ref.");
        }

        return await apiClient.DownloadContentStringAsync(command.PayloadRef, cancellationToken);
    }

    private async Task PublishEventAsync(
        RunnerCommandEnvelope command,
        string eventType,
        ClaimCheckContentRef? payloadRef,
        CancellationToken cancellationToken)
    {
        await apiClient.PublishEventAsync(
            new RunnerEventEnvelope(
                Id: $"event_{Guid.NewGuid():N}",
                RunnerId: _options.RunnerId,
                AgentSessionId: command.AgentSessionId,
                CommandId: command.Id,
                Type: eventType,
                PayloadRef: payloadRef,
                CausationId: command.Id,
                CorrelationId: command.CorrelationId,
                CreatedAt: DateTimeOffset.UtcNow),
            cancellationToken);
    }

    private string SessionRoot(string sessionId)
    {
        return Path.Combine(_options.RuntimeRoot, "sessions", sessionId);
    }
}

public interface IHarnessProcessRunner
{
    Task<ProcessOutput> RunAsync(string fileName, string[] arguments, CancellationToken cancellationToken);
}

public sealed class HarnessProcessRunner(IOptions<RunnerOptions> options) : IHarnessProcessRunner
{
    private readonly RunnerOptions _options = options.Value;

    public async Task<ProcessOutput> RunAsync(
        string fileName,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        var command = string.Join(" ", new[] { fileName }.Concat(arguments.Select(Quote)));
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = LocateHarnessRepo(),
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessOutput(command, process.ExitCode, await stdoutTask, await stderrTask);
    }

    private string LocateHarnessRepo()
    {
        if (!string.IsNullOrWhiteSpace(_options.HarnessRepoRoot))
        {
            return Path.GetFullPath(_options.HarnessRepoRoot);
        }

        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(Path.Combine(current, "pyproject.toml")) && Directory.Exists(Path.Combine(current, "src", "lamplighter_opencode")))
            {
                return current;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent == current)
            {
                break;
            }
            current = parent ?? "";
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    }

    private static string Quote(string value)
    {
        return value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
    }
}

public sealed record ProcessOutput(string Command, int ExitCode, string Stdout, string Stderr);
