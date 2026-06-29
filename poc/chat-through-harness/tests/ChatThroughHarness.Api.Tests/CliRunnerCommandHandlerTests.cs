namespace ChatThroughHarness.Api.Tests;

using ChatThroughHarness.Protocol;
using ChatThroughHarness.Runner;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

public sealed class CliRunnerCommandHandlerTests
{
    [Fact]
    public async Task PrepareSessionCommandInvokesHarnessCliAndPublishesReadyEvent()
    {
        var contentRef = new ClaimCheckContentRef("tradecraft://content/spec_1", "sha", "application/json", 2);
        var api = new FakeRunnerApiClient("""{"agent_session_id":"session_1","workspace_ref":"/tmp/workspace"}""");
        var process = new FakeHarnessProcessRunner(
            new ProcessOutput(
                Command: "uv run lamplighter-opencode prepare-session",
                ExitCode: 0,
                Stdout: """{"status":"prepared"}""",
                Stderr: ""),
            new ProcessOutput(
                Command: "uv run lamplighter-opencode start-session",
                ExitCode: 0,
                Stdout: """{"status":"ready","endpoint":"http://127.0.0.1:4097"}""",
                Stderr: ""));
        var handler = new CliRunnerCommandHandler(
            api,
            process,
            Options.Create(new RunnerOptions { RunnerId = "runner_1", RuntimeRoot = NewRuntimeRoot() }),
            NullLogger<CliRunnerCommandHandler>.Instance);
        var command = new RunnerCommandEnvelope(
            Id: "cmd_1",
            RunnerId: "runner_1",
            AgentSessionId: "session_1",
            Type: RunnerCommandTypes.PrepareAgentSession,
            Status: RunnerCommandStatuses.Claimed,
            PayloadRef: contentRef,
            CorrelationId: "corr_1",
            IdempotencyKey: "session_1",
            CreatedAt: DateTimeOffset.UnixEpoch,
            AvailableAt: DateTimeOffset.UnixEpoch,
            Lease: new RunnerCommandLease("lease_1", "runner_1", DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow.AddMinutes(1), 1));

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.Equal(RunnerCommandStatuses.Completed, result.Status);
        Assert.Collection(
            process.Invocations,
            invocation =>
            {
                Assert.Equal("uv", invocation.FileName);
                Assert.Contains("prepare-session", invocation.Arguments);
                Assert.Contains("--spec", invocation.Arguments);
                Assert.Contains("--runtime-root", invocation.Arguments);
            },
            invocation =>
            {
                Assert.Equal("uv", invocation.FileName);
                Assert.Contains("start-session", invocation.Arguments);
                Assert.Contains("--session", invocation.Arguments);
                Assert.Contains("--runtime-root", invocation.Arguments);
            });
        Assert.Single(api.PublishedEvents);
        Assert.Equal(RunnerEventTypes.AgentSessionReady, api.PublishedEvents.Single().Type);
        Assert.Equal("application/vnd.tradecraft.start-session-result+json", api.Uploads.Single().ContentType);
    }

    [Fact]
    public async Task SubmitTurnCommandReturnsFailureForNonZeroHarnessExit()
    {
        var contentRef = new ClaimCheckContentRef("tradecraft://content/turn_1", "sha", "application/json", 2);
        var api = new FakeRunnerApiClient("""{"id":"turn_1","agent_session_id":"session_1","instruction":"hello"}""");
        var process = new FakeHarnessProcessRunner(new ProcessOutput(
            Command: "uv run lamplighter-opencode submit-turn",
            ExitCode: 2,
            Stdout: "",
            Stderr: "boom"));
        var handler = new CliRunnerCommandHandler(
            api,
            process,
            Options.Create(new RunnerOptions { RunnerId = "runner_1", RuntimeRoot = NewRuntimeRoot() }),
            NullLogger<CliRunnerCommandHandler>.Instance);
        var command = new RunnerCommandEnvelope(
            Id: "cmd_1",
            RunnerId: "runner_1",
            AgentSessionId: "session_1",
            Type: RunnerCommandTypes.SubmitAgentTurn,
            Status: RunnerCommandStatuses.Claimed,
            PayloadRef: contentRef,
            CorrelationId: "turn_1",
            IdempotencyKey: "turn_1",
            CreatedAt: DateTimeOffset.UnixEpoch,
            AvailableAt: DateTimeOffset.UnixEpoch,
            Lease: new RunnerCommandLease("lease_1", "runner_1", DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow.AddMinutes(1), 1));

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.Equal(RunnerCommandStatuses.Failed, result.Status);
        Assert.NotNull(result.Failure);
        Assert.Contains("submit-turn", process.Arguments);
        Assert.Single(api.PublishedEvents);
        Assert.Equal(RunnerEventTypes.AgentTurnFailed, api.PublishedEvents.Single().Type);
        Assert.Equal("text/plain", api.Uploads.Single().ContentType);
    }

    [Fact]
    public async Task SubmitTurnCommandPublishesFailedEventForStructuredFailure()
    {
        var contentRef = new ClaimCheckContentRef("tradecraft://content/turn_1", "sha", "application/json", 2);
        var api = new FakeRunnerApiClient("""{"id":"turn_1","agent_session_id":"session_1","instruction":"hello"}""");
        var process = new FakeHarnessProcessRunner(new ProcessOutput(
            Command: "uv run lamplighter-opencode submit-turn",
            ExitCode: 0,
            Stdout: """
                {
                  "id": "result_1",
                  "agent_session_id": "session_1",
                  "request_id": "turn_1",
                  "status": "failed",
                  "message": "OpenCode server request failed.",
                  "artifact_refs": [],
                  "changed_files": [],
                  "commands_observed": [],
                  "failure_report": {
                    "summary": "OpenCode server request failed.",
                    "detail": "Connection refused"
                  }
                }
                """,
            Stderr: ""));
        var handler = new CliRunnerCommandHandler(
            api,
            process,
            Options.Create(new RunnerOptions { RunnerId = "runner_1", RuntimeRoot = NewRuntimeRoot() }),
            NullLogger<CliRunnerCommandHandler>.Instance);
        var command = new RunnerCommandEnvelope(
            Id: "cmd_1",
            RunnerId: "runner_1",
            AgentSessionId: "session_1",
            Type: RunnerCommandTypes.SubmitAgentTurn,
            Status: RunnerCommandStatuses.Claimed,
            PayloadRef: contentRef,
            CorrelationId: "turn_1",
            IdempotencyKey: "turn_1",
            CreatedAt: DateTimeOffset.UnixEpoch,
            AvailableAt: DateTimeOffset.UnixEpoch,
            Lease: new RunnerCommandLease("lease_1", "runner_1", DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow.AddMinutes(1), 1));

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.Equal(RunnerCommandStatuses.Completed, result.Status);
        Assert.Single(api.PublishedEvents);
        Assert.Equal(RunnerEventTypes.AgentTurnFailed, api.PublishedEvents.Single().Type);
        Assert.Equal("application/vnd.tradecraft.agent-turn-result+json", api.Uploads.Single().ContentType);
    }

    private static string NewRuntimeRoot()
    {
        return Path.Combine(Path.GetTempPath(), Ids.New("runner_handler"));
    }

    private sealed class FakeHarnessProcessRunner : IHarnessProcessRunner
    {
        private readonly Queue<ProcessOutput> _outputs;

        public string FileName { get; private set; } = "";
        public string[] Arguments { get; private set; } = [];
        public List<ProcessInvocation> Invocations { get; } = [];

        public FakeHarnessProcessRunner(params ProcessOutput[] outputs)
        {
            _outputs = new Queue<ProcessOutput>(outputs);
        }

        public Task<ProcessOutput> RunAsync(string fileName, string[] arguments, CancellationToken cancellationToken)
        {
            FileName = fileName;
            Arguments = arguments;
            Invocations.Add(new ProcessInvocation(fileName, arguments));
            return Task.FromResult(_outputs.Dequeue());
        }
    }

    private sealed record ProcessInvocation(string FileName, string[] Arguments);

    private sealed class FakeRunnerApiClient(string payload) : IRunnerApiClient
    {
        public List<RunnerEventEnvelope> PublishedEvents { get; } = [];
        public List<ClaimCheckContentRef> Uploads { get; } = [];

        public Task UpsertHeartbeatAsync(RunnerHeartbeat heartbeat, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyCollection<RunnerCommandEnvelope>> PollCommandsAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyCollection<RunnerCommandEnvelope>>([]);
        }

        public Task<RunnerCommandEnvelope> ClaimCommandAsync(RunnerCommandEnvelope command, CancellationToken cancellationToken)
        {
            return Task.FromResult(command);
        }

        public Task CompleteCommandAsync(RunnerCommandEnvelope command, RunnerCommandResult result, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<string> DownloadContentStringAsync(ClaimCheckContentRef contentRef, CancellationToken cancellationToken)
        {
            return Task.FromResult(payload);
        }

        public Task<byte[]> DownloadContentBytesAsync(ClaimCheckContentRef contentRef, CancellationToken cancellationToken)
        {
            return Task.FromResult(System.Text.Encoding.UTF8.GetBytes(payload));
        }

        public Task<ClaimCheckContentRef> UploadContentAsync(string content, string contentType, CancellationToken cancellationToken)
        {
            var contentRef = new ClaimCheckContentRef($"tradecraft://content/upload_{Uploads.Count}", "sha", contentType, content.Length);
            Uploads.Add(contentRef);
            return Task.FromResult(contentRef);
        }

        public Task<ClaimCheckContentRef> UploadContentAsync(byte[] bytes, string contentType, CancellationToken cancellationToken)
        {
            var contentRef = new ClaimCheckContentRef($"tradecraft://content/upload_{Uploads.Count}", "sha", contentType, bytes.Length);
            Uploads.Add(contentRef);
            return Task.FromResult(contentRef);
        }

        public Task PublishEventAsync(RunnerEventEnvelope runnerEvent, CancellationToken cancellationToken)
        {
            PublishedEvents.Add(runnerEvent);
            return Task.CompletedTask;
        }
    }
}
