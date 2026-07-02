using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ChatThroughHarness.Protocol.V1;

namespace ChatThroughHarness.Runner;

public interface IRunnerCommandHandler
{
    Task<RunnerCommandResult> HandleAsync(
        ControllerCommand command,
        CancellationToken cancellationToken);
}

public sealed record RunnerCommandResult(
    string DeliveryStatus,
    ContentReference? ResultRef,
    ProtocolError? Error)
{
    public static RunnerCommandResult Completed(ContentReference? resultRef = null)
    {
        return new RunnerCommandResult(CommandDeliveryStatuses.Completed, resultRef, null);
    }

    public static RunnerCommandResult Failed(ProtocolError error)
    {
        return new RunnerCommandResult(CommandDeliveryStatuses.Failed, null, error);
    }
}

public sealed class CliRunnerCommandHandler(
    IRunnerApiClient apiClient,
    IHarnessProcessRunner processRunner,
    IOptions<RunnerOptions> options,
    ILogger<CliRunnerCommandHandler> logger) : IRunnerCommandHandler
{
    private readonly RunnerOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, long> _aggregateSequences = new();

    public async Task<RunnerCommandResult> HandleAsync(
        ControllerCommand command,
        CancellationToken cancellationToken)
    {
        return command.CommandType switch
        {
            ControllerCommandTypes.StartAgentRuntime =>
                await PrepareAgentAsync(command, cancellationToken),
            ControllerCommandTypes.StopAgentRuntime =>
                await StopAgentAsync(command, cancellationToken),
            ControllerCommandTypes.CreateAgentSession =>
                await CreateSessionAsync(command, cancellationToken),
            ControllerCommandTypes.StartInvocation =>
                await SubmitTurnAsync(command, cancellationToken),
            ControllerCommandTypes.SynchronizeSessionHistory =>
                await SyncSessionHistoryAsync(command, cancellationToken),
            ControllerCommandTypes.CloseAgentSession =>
                await CancelSessionAsync(command, cancellationToken),
            _ => RunnerCommandResult.Failed(new ProtocolError(
                Code: "unsupported_controller_command",
                Classification: ProtocolErrorClassifications.UnsupportedOperation,
                Summary: $"Unsupported command type {command.CommandType}.",
                Retryable: false))
        };
    }

    private async Task<RunnerCommandResult> PrepareAgentAsync(
        ControllerCommand command,
        CancellationToken cancellationToken)
    {
        var agentId = RequiredAgentId(command);
        var specPath = await WriteCommandPayloadAsync(command, "agent-spec.json", cancellationToken);
        var prepareOutput = await processRunner.RunAsync(
            "uv",
            [
                "run", "lamplighter-opencode", "prepare-agent",
                "--spec", specPath,
                "--controller-workspace", _options.ControllerWorkspace,
                "--json"
            ],
            cancellationToken);
        if (prepareOutput.ExitCode != 0)
        {
            return await CompleteFromProcessAsync(
                command,
                prepareOutput,
                ControllerEventTypes.AgentRuntimeReady,
                ControllerEventTypes.AgentRuntimeLost,
                "application/vnd.tradecraft.prepare-agent-result+json",
                cancellationToken);
        }

        var startOutput = await processRunner.RunAsync(
            "uv",
            [
                "run", "lamplighter-opencode", "start-agent",
                "--agent", agentId,
                "--controller-workspace", _options.ControllerWorkspace,
                "--json"
            ],
            cancellationToken);
        return await CompleteFromProcessAsync(
            command,
            startOutput,
            ControllerEventTypes.AgentRuntimeReady,
            ControllerEventTypes.AgentRuntimeLost,
            "application/vnd.tradecraft.start-agent-result+json",
            cancellationToken);
    }

    private async Task<RunnerCommandResult> CreateSessionAsync(
        ControllerCommand command,
        CancellationToken cancellationToken)
    {
        var agentId = RequiredAgentId(command);
        var specPath = await WriteCommandPayloadAsync(command, "session-spec.json", cancellationToken);
        var output = await processRunner.RunAsync(
            "uv",
            [
                "run", "lamplighter-opencode", "create-session",
                "--agent", agentId,
                "--spec", specPath,
                "--controller-workspace", _options.ControllerWorkspace,
                "--json"
            ],
            cancellationToken);
        return await CompleteFromProcessAsync(
            command,
            output,
            ControllerEventTypes.AgentSessionCreated,
            ControllerEventTypes.AgentSessionClosed,
            "application/vnd.tradecraft.create-session-result+json",
            cancellationToken);
    }

    private async Task<RunnerCommandResult> StopAgentAsync(
        ControllerCommand command,
        CancellationToken cancellationToken)
    {
        var output = await processRunner.RunAsync(
            "uv",
            [
                "run", "lamplighter-opencode", "stop-agent",
                "--agent", RequiredAgentId(command),
                "--controller-workspace", _options.ControllerWorkspace,
                "--json"
            ],
            cancellationToken);
        return await CompleteFromProcessAsync(
            command,
            output,
            ControllerEventTypes.AgentRuntimeStopped,
            ControllerEventTypes.AgentRuntimeLost,
            "application/vnd.tradecraft.stop-agent-result+json",
            cancellationToken);
    }

    private async Task<RunnerCommandResult> PrepareSessionAsync(
        ControllerCommand command,
        CancellationToken cancellationToken)
    {
        var payload = await DownloadPayloadAsync(command, cancellationToken);
        var sessionId = RequiredAgentSessionId(command);
        var sessionRoot = SessionRoot(sessionId);
        Directory.CreateDirectory(sessionRoot);
        var specPath = Path.Combine(sessionRoot, "agent-session-spec.json");
        await File.WriteAllTextAsync(specPath, payload, cancellationToken);

        var prepareOutput = await processRunner.RunAsync(
            "uv",
            ["run", "lamplighter-opencode", "prepare-session", "--spec", specPath, "--runtime-root", _options.ControllerWorkspace, "--json"],
            cancellationToken);

        if (prepareOutput.ExitCode != 0)
        {
            return await CompleteFromProcessAsync(
                command,
                prepareOutput,
                successEventType: ControllerEventTypes.AgentSessionCreated,
                failureEventType: ControllerEventTypes.AgentSessionClosed,
                successContentType: "application/vnd.tradecraft.prepare-session-result+json",
                cancellationToken);
        }

        var startOutput = await processRunner.RunAsync(
            "uv",
            ["run", "lamplighter-opencode", "start-session", "--session", sessionId, "--runtime-root", _options.ControllerWorkspace, "--json"],
            cancellationToken);

        return await CompleteFromProcessAsync(
            command,
            startOutput,
            successEventType: ControllerEventTypes.AgentSessionCreated,
            failureEventType: ControllerEventTypes.AgentSessionClosed,
            successContentType: "application/vnd.tradecraft.start-session-result+json",
            cancellationToken);
    }

    private async Task<RunnerCommandResult> SubmitTurnAsync(
        ControllerCommand command,
        CancellationToken cancellationToken)
    {
        var payload = await DownloadPayloadAsync(command, cancellationToken);
        var sessionId = RequiredAgentSessionId(command);
        var runtimeId = command.Target.RuntimeId;
        var sessionRoot = runtimeId is null
            ? SessionRoot(sessionId)
            : AgentSessionRoot(runtimeId, sessionId);
        Directory.CreateDirectory(sessionRoot);
        var requestPath = Path.Combine(sessionRoot, $"{InvocationId(command)}.request.json");
        await File.WriteAllTextAsync(requestPath, payload, cancellationToken);

        var arguments = new List<string>
        {
            "run", "lamplighter-opencode", "submit-turn",
            "--session", sessionId,
            "--request", requestPath
        };
        if (runtimeId is not null)
        {
            arguments.AddRange([
                "--agent", runtimeId,
                "--controller-workspace", _options.ControllerWorkspace
            ]);
        }
        arguments.Add("--json");
        var output = await processRunner.RunAsync("uv", [.. arguments], cancellationToken);

        return await CompleteTurnFromProcessAsync(command, output, cancellationToken);
    }

    private async Task<RunnerCommandResult> CancelSessionAsync(
        ControllerCommand command,
        CancellationToken cancellationToken)
    {
        var sessionId = RequiredAgentSessionId(command);
        var runtimeId = command.Target.RuntimeId;
        logger.LogInformation(
            "Received close command {CommandId} for session {SessionId}.",
            command.CommandId,
            sessionId);
        if (runtimeId is not null)
        {
            var output = await processRunner.RunAsync(
                "uv",
                [
                    "run", "lamplighter-opencode", "cancel-session",
                    "--agent", runtimeId,
                    "--session", sessionId,
                    "--controller-workspace", _options.ControllerWorkspace,
                    "--json"
                ],
                cancellationToken);
            return await CompleteFromProcessAsync(
                command,
                output,
                ControllerEventTypes.AgentSessionClosed,
                ControllerEventTypes.AgentSessionClosed,
                "application/vnd.tradecraft.cancel-session-result+json",
                cancellationToken);
        }
        var payloadRef = command.PayloadRef is null
            ? null
            : await apiClient.UploadContentAsync(
                await apiClient.DownloadContentStringAsync(command.PayloadRef, cancellationToken),
                command.PayloadRef.ContentType,
                cancellationToken);
        await PublishEventAsync(
            command,
            ControllerEventTypes.AgentSessionClosed,
            payloadRef,
            cancellationToken);
        return RunnerCommandResult.Completed(payloadRef);
    }

    private async Task<RunnerCommandResult> SyncSessionHistoryAsync(
        ControllerCommand command,
        CancellationToken cancellationToken)
    {
        var sessionId = RequiredAgentSessionId(command);
        var output = await processRunner.RunAsync(
            "uv",
            [
                "run", "lamplighter-opencode", "get-session-history",
                "--agent", RequiredAgentId(command),
                "--session", sessionId,
                "--controller-workspace", _options.ControllerWorkspace,
                "--json"
            ],
            cancellationToken);
        return await CompleteFromProcessAsync(
            command,
            output,
            ControllerEventTypes.AgentSessionHistorySynchronized,
            ControllerEventTypes.AgentSessionHistorySynchronizationFailed,
            "application/vnd.tradecraft.agent-chat-history+json",
            cancellationToken);
    }

    private async Task<RunnerCommandResult> CompleteFromProcessAsync(
        ControllerCommand command,
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

        return await CompleteProcessFailureAsync(command, output, failureEventType, cancellationToken);
    }

    private async Task<RunnerCommandResult> CompleteTurnFromProcessAsync(
        ControllerCommand command,
        ProcessOutput output,
        CancellationToken cancellationToken)
    {
        if (output.ExitCode != 0)
        {
            return await CompleteProcessFailureAsync(
                command,
                output,
                ControllerEventTypes.OutcomeReported,
                cancellationToken);
        }

        string? resultStatus;
        try
        {
            using var result = JsonDocument.Parse(output.Stdout);
            resultStatus = result.RootElement.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                    ? status.GetString()
                    : null;
        }
        catch (JsonException ex)
        {
            var invalidOutput = output with
            {
                ExitCode = -1,
                Stderr = $"Harness returned invalid AgentTurnResult JSON: {ex.Message}"
            };
            return await CompleteProcessFailureAsync(
                command,
                invalidOutput,
                ControllerEventTypes.OutcomeReported,
                cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(resultStatus))
        {
            var invalidOutput = output with
            {
                ExitCode = -1,
                Stderr = "Harness AgentTurnResult JSON did not contain a status."
            };
            return await CompleteProcessFailureAsync(
                command,
                invalidOutput,
                ControllerEventTypes.OutcomeReported,
                cancellationToken);
        }

        var resultRef = await apiClient.UploadContentAsync(
            output.Stdout,
            "application/vnd.tradecraft.agent-turn-result+json",
            cancellationToken);
        await PublishEventAsync(
            command,
            ControllerEventTypes.OutcomeReported,
            resultRef,
            cancellationToken);
        return RunnerCommandResult.Completed(resultRef);
    }

    private async Task<RunnerCommandResult> CompleteProcessFailureAsync(
        ControllerCommand command,
        ProcessOutput output,
        string failureEventType,
        CancellationToken cancellationToken)
    {
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
        var failure = new ProtocolError(
            Code: "runtime_adapter_command_failed",
            Classification: ProtocolErrorClassifications.AdapterFailure,
            Summary: $"Harness command failed with exit code {output.ExitCode}.",
            Retryable: false,
            DiagnosticRef: failureRef);
        await PublishEventAsync(command, failureEventType, failureRef, cancellationToken);
        return RunnerCommandResult.Failed(failure);
    }

    private async Task<string> DownloadPayloadAsync(
        ControllerCommand command,
        CancellationToken cancellationToken)
    {
        if (command.PayloadRef is null)
        {
            throw new InvalidOperationException(
                $"Command {command.CommandId} does not have a payload_ref.");
        }

        return await apiClient.DownloadContentStringAsync(command.PayloadRef, cancellationToken);
    }

    private async Task PublishEventAsync(
        ControllerCommand command,
        string eventType,
        ContentReference? payloadRef,
        CancellationToken cancellationToken)
    {
        var aggregate = Aggregate(command);
        var sequence = _aggregateSequences.AddOrUpdate(
            $"{aggregate.Type}:{aggregate.Id}",
            1,
            static (_, current) => current + 1);
        await apiClient.PublishEventAsync(
            new ControllerEvent(
                MessageType: ControllerMessageTypes.Event,
                ProtocolVersion: ControllerProtocolVersions.Protocol,
                SchemaVersion: ControllerProtocolVersions.Schema,
                EventId: $"event_{Guid.NewGuid():N}",
                ControllerId: _options.RunnerId,
                EventType: eventType,
                Aggregate: aggregate,
                Sequence: sequence,
                OccurredAt: DateTimeOffset.UtcNow,
                PayloadSchemaVersion: ControllerProtocolVersions.Schema,
                Target: command.Target,
                Correlation: command.Correlation with
                {
                    CommandId = command.CommandId,
                    CausationId = command.CommandId
                },
                FencingToken: command.Execution.Lease?.FencingToken,
                PayloadRef: payloadRef,
                Payload: payloadRef is null
                    ? JsonSerializer.SerializeToElement(new { })
                    : null),
            cancellationToken);
    }

    private string SessionRoot(string sessionId)
    {
        return Path.Combine(_options.ControllerWorkspace, "sessions", sessionId);
    }

    private string AgentSessionRoot(string agentId, string sessionId)
    {
        return Path.Combine(
            _options.ControllerWorkspace,
            "agents",
            SafeIdentifier(agentId, "agent_"),
            "runtime",
            "sessions",
            SafeIdentifier(sessionId, "session_"));
    }

    private async Task<string> WriteCommandPayloadAsync(
        ControllerCommand command,
        string fileName,
        CancellationToken cancellationToken)
    {
        var payload = await DownloadPayloadAsync(command, cancellationToken);
        var commandRoot = Path.Combine(
            _options.ControllerWorkspace,
            "commands",
            SafeIdentifier(command.CommandId, "cmd_"));
        Directory.CreateDirectory(commandRoot);
        var path = Path.Combine(commandRoot, fileName);
        await File.WriteAllTextAsync(path, payload, cancellationToken);
        return path;
    }

    private static string RequiredAgentId(ControllerCommand command)
    {
        return SafeIdentifier(
            command.Target.RuntimeId
            ?? throw new InvalidOperationException(
                $"Command {command.CommandId} does not target a runtime."),
            "agent_");
    }

    private static string RequiredAgentSessionId(ControllerCommand command)
    {
        return command.Target.AgentSessionId
               ?? throw new InvalidOperationException(
                   $"Command {command.CommandId} does not target an agent session.");
    }

    private static string InvocationId(ControllerCommand command)
    {
        return command.Target.InvocationId
               ?? command.Correlation.InvocationId
               ?? command.Correlation.CorrelationId
               ?? command.CommandId;
    }

    private static AggregateReference Aggregate(ControllerCommand command)
    {
        if (command.Target.InvocationId is { } invocationId)
        {
            return new AggregateReference("invocation", invocationId);
        }

        if (command.Target.AgentSessionId is { } sessionId)
        {
            return new AggregateReference("agent_session", sessionId);
        }

        if (command.Target.RuntimeId is { } runtimeId)
        {
            return new AggregateReference("runtime", runtimeId);
        }

        return new AggregateReference("controller", command.Target.ControllerId ?? "controller_unknown");
    }

    private static string SafeIdentifier(string value, string prefix)
    {
        if (!value.StartsWith(prefix, StringComparison.Ordinal)
            || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            throw new InvalidOperationException($"Unsafe identifier: {value}");
        }
        return value;
    }
}

public interface IHarnessProcessRunner
{
    Task<ProcessOutput> RunAsync(string fileName, string[] arguments, CancellationToken cancellationToken);
}

public sealed class HarnessProcessRunner(
    IOptions<RunnerOptions> options,
    ILogger<HarnessProcessRunner> logger) : IHarnessProcessRunner
{
    private readonly RunnerOptions _options = options.Value;

    public async Task<ProcessOutput> RunAsync(
        string fileName,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        var command = string.Join(" ", new[] { fileName }.Concat(arguments.Select(Quote)));
        var startedAt = Stopwatch.GetTimestamp();
        logger.LogInformation("Starting harness process: {Command}", command);
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
        var output = new ProcessOutput(command, process.ExitCode, await stdoutTask, await stderrTask);
        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        if (output.ExitCode == 0)
        {
            logger.LogInformation(
                "Harness process completed with exit code 0 in {ElapsedMilliseconds} ms.",
                elapsed.TotalMilliseconds);
        }
        else
        {
            logger.LogWarning(
                "Harness process failed with exit code {ExitCode} in {ElapsedMilliseconds} ms; stderr length {StderrLength}.",
                output.ExitCode,
                elapsed.TotalMilliseconds,
                output.Stderr.Length);
        }

        return output;
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
