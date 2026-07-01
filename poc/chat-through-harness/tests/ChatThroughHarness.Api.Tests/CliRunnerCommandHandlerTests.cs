namespace ChatThroughHarness.Api.Tests;

using ChatThroughHarness.Protocol.V1;
using ChatThroughHarness.Runner;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

public sealed class CliRunnerCommandHandlerTests
{
    [Fact]
    public async Task HandleAsync_WhenStartingRuntime_ThenPublishesCanonicalReadyEvent()
    {
        // Arrange
        var contentRef = Content("agent_1", "application/json", 2);
        var api = new FakeRunnerApiClient("""{"agent_id":"agent_1"}""");
        var process = new FakeHarnessProcessRunner(
            new ProcessOutput("prepare-agent", 0, """{"status":"allocated"}""", ""),
            new ProcessOutput("start-agent", 0, """{"status":"ready"}""", ""));
        var controllerWorkspace = NewRuntimeRoot();
        var handler = CreateHandler(api, process, controllerWorkspace);
        var command = Command(
            ControllerCommandTypes.StartAgentRuntime,
            contentRef,
            runtimeId: "agent_1");

        // Act
        var result = await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.Equal(CommandDeliveryStatuses.Completed, result.DeliveryStatus);
        Assert.Collection(
            process.Invocations,
            invocation =>
            {
                Assert.Contains("prepare-agent", invocation.Arguments);
                Assert.Contains(controllerWorkspace, invocation.Arguments);
            },
            invocation =>
            {
                Assert.Contains("start-agent", invocation.Arguments);
                Assert.Contains("agent_1", invocation.Arguments);
                Assert.Contains(controllerWorkspace, invocation.Arguments);
            });
        var published = Assert.Single(api.PublishedEvents);
        Assert.Equal(ControllerEventTypes.AgentRuntimeReady, published.EventType);
        Assert.Equal("agent_1", published.Target.RuntimeId);
        Assert.Equal("runtime", published.Aggregate.Type);
    }

    [Fact]
    public async Task HandleAsync_WhenCreatingAgentSession_ThenPublishesCanonicalSessionEvent()
    {
        // Arrange
        var contentRef = Content("spec_1", "application/json", 2);
        var api = new FakeRunnerApiClient(
            """{"agent_session_id":"session_1","workspace_ref":"/tmp/workspace"}""");
        var process = new FakeHarnessProcessRunner(
            new ProcessOutput(
                "uv run lamplighter-opencode create-session",
                0,
                """{"status":"ready"}""",
                ""));
        var handler = CreateHandler(api, process, NewRuntimeRoot());
        var command = Command(
            ControllerCommandTypes.CreateAgentSession,
            contentRef,
            runtimeId: "agent_1",
            sessionId: "session_1");

        // Act
        var result = await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.Equal(CommandDeliveryStatuses.Completed, result.DeliveryStatus);
        Assert.Contains("create-session", process.Arguments);
        var published = Assert.Single(api.PublishedEvents);
        Assert.Equal(ControllerEventTypes.AgentSessionCreated, published.EventType);
        Assert.Equal("session_1", published.Target.AgentSessionId);
        Assert.Equal("agent_session", published.Aggregate.Type);
    }

    [Fact]
    public async Task HandleAsync_WhenInvocationAdapterFails_ThenReturnsClassifiedFailure()
    {
        // Arrange
        var api = new FakeRunnerApiClient(
            """{"id":"turn_1","agent_session_id":"session_1","instruction":"hello"}""");
        var process = new FakeHarnessProcessRunner(
            new ProcessOutput(
                "uv run lamplighter-opencode submit-turn",
                2,
                "",
                "boom"));
        var handler = CreateHandler(api, process, NewRuntimeRoot());
        var command = Command(
            ControllerCommandTypes.StartInvocation,
            Content("turn_1", "application/json", 2),
            runtimeId: "agent_1",
            sessionId: "session_1",
            invocationId: "turn_1");

        // Act
        var result = await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.Equal(CommandDeliveryStatuses.Failed, result.DeliveryStatus);
        Assert.Equal(
            ProtocolErrorClassifications.AdapterFailure,
            Assert.IsType<ProtocolError>(result.Error).Classification);
        Assert.Contains("submit-turn", process.Arguments);
        var published = Assert.Single(api.PublishedEvents);
        Assert.Equal(ControllerEventTypes.OutcomeReported, published.EventType);
        Assert.Equal("invocation", published.Aggregate.Type);
        Assert.Equal("text/plain", api.Uploads.Single().ContentType);
    }

    [Fact]
    public async Task HandleAsync_WhenSynchronizingHistory_ThenPublishesCanonicalHistoryEvent()
    {
        // Arrange
        var history = """
            {
              "agent_id": "agent_1",
              "agent_session_id": "session_1",
              "opencode_session_id": "oc_session_1",
              "observed_at": "2026-06-30T00:00:00Z",
              "messages": [],
              "raw_messages": []
            }
            """;
        var api = new FakeRunnerApiClient("");
        var process = new FakeHarnessProcessRunner(
            new ProcessOutput("get-session-history", 0, history, ""));
        var controllerWorkspace = NewRuntimeRoot();
        var handler = CreateHandler(api, process, controllerWorkspace);
        var command = Command(
            ControllerCommandTypes.SynchronizeSessionHistory,
            payloadRef: null,
            runtimeId: "agent_1",
            sessionId: "session_1");

        // Act
        var result = await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.Equal(CommandDeliveryStatuses.Completed, result.DeliveryStatus);
        Assert.Contains("get-session-history", process.Arguments);
        Assert.Contains("agent_1", process.Arguments);
        Assert.Contains("session_1", process.Arguments);
        Assert.Contains(controllerWorkspace, process.Arguments);
        Assert.Equal(
            "application/vnd.tradecraft.agent-chat-history+json",
            api.Uploads.Single().ContentType);
        Assert.Equal(
            ControllerEventTypes.AgentSessionHistorySynchronized,
            api.PublishedEvents.Single().EventType);
    }

    [Fact]
    public async Task HandleAsync_WhenInvocationReturnsStructuredFailure_ThenDeliveryStillCompletes()
    {
        // Arrange
        var api = new FakeRunnerApiClient(
            """{"id":"turn_1","agent_session_id":"session_1","instruction":"hello"}""");
        var process = new FakeHarnessProcessRunner(new ProcessOutput(
            "uv run lamplighter-opencode submit-turn",
            0,
            """
            {
              "status": "failed",
              "message": "OpenCode server request failed.",
              "failure_report": {
                "summary": "OpenCode server request failed.",
                "detail": "Connection refused"
              }
            }
            """,
            ""));
        var handler = CreateHandler(api, process, NewRuntimeRoot());
        var command = Command(
            ControllerCommandTypes.StartInvocation,
            Content("turn_1", "application/json", 2),
            runtimeId: "agent_1",
            sessionId: "session_1",
            invocationId: "turn_1");

        // Act
        var result = await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.Equal(CommandDeliveryStatuses.Completed, result.DeliveryStatus);
        Assert.Null(result.Error);
        Assert.Equal(
            ControllerEventTypes.OutcomeReported,
            api.PublishedEvents.Single().EventType);
        Assert.Equal(
            "application/vnd.tradecraft.agent-turn-result+json",
            api.Uploads.Single().ContentType);
    }

    private static CliRunnerCommandHandler CreateHandler(
        IRunnerApiClient api,
        IHarnessProcessRunner process,
        string controllerWorkspace)
    {
        return new CliRunnerCommandHandler(
            api,
            process,
            Options.Create(new RunnerOptions
            {
                RunnerId = "controller_1",
                ControllerWorkspace = controllerWorkspace
            }),
            NullLogger<CliRunnerCommandHandler>.Instance);
    }

    private static ControllerCommand Command(
        string commandType,
        ContentReference? payloadRef,
        string? runtimeId = null,
        string? sessionId = null,
        string? invocationId = null)
    {
        var now = DateTimeOffset.UnixEpoch;
        return new ControllerCommand(
            MessageType: ControllerMessageTypes.Command,
            ProtocolVersion: ControllerProtocolVersions.Protocol,
            SchemaVersion: ControllerProtocolVersions.Schema,
            CommandId: "cmd_1",
            CommandType: commandType,
            IdempotencyKey: "idempotency_1",
            IssuedAt: now,
            Target: new ResourceTarget(
                ControllerId: "controller_1",
                RuntimeId: runtimeId,
                AgentSessionId: sessionId,
                InvocationId: invocationId),
            Correlation: new ProtocolCorrelation(
                CommandId: "cmd_1",
                CorrelationId: invocationId ?? sessionId ?? runtimeId,
                InvocationId: invocationId),
            AuthorizationContext: new AuthorizationContext(
                "system://tests",
                "authorization-grant://tests/1",
                now),
            Execution: new CommandExecution(
                now.AddHours(1),
                "PT30S",
                new CommandLease(
                    "lease_1",
                    "controller_1",
                    now,
                    now.AddMinutes(1),
                    1,
                    1)),
            PayloadRef: payloadRef);
    }

    private static ContentReference Content(
        string id,
        string contentType,
        long length)
    {
        return new ContentReference(
            $"tradecraft://content/{id}",
            "sha",
            contentType,
            length);
    }

    private static string NewRuntimeRoot()
    {
        return Path.Combine(Path.GetTempPath(), Ids.New("runner_handler"));
    }

    private sealed class FakeHarnessProcessRunner(params ProcessOutput[] outputs)
        : IHarnessProcessRunner
    {
        private readonly Queue<ProcessOutput> _outputs = new(outputs);

        public string[] Arguments { get; private set; } = [];

        public List<ProcessInvocation> Invocations { get; } = [];

        public Task<ProcessOutput> RunAsync(
            string fileName,
            string[] arguments,
            CancellationToken cancellationToken)
        {
            Arguments = arguments;
            Invocations.Add(new ProcessInvocation(fileName, arguments));
            return Task.FromResult(_outputs.Dequeue());
        }
    }

    private sealed record ProcessInvocation(string FileName, string[] Arguments);

    private sealed class FakeRunnerApiClient(string payload) : IRunnerApiClient
    {
        public List<ControllerEvent> PublishedEvents { get; } = [];

        public List<ContentReference> Uploads { get; } = [];

        public Task UpsertHeartbeatAsync(
            ControllerHeartbeat heartbeat,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyCollection<ControllerCommand>> PollCommandsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<ControllerCommand>>([]);

        public Task<ControllerCommand> AcknowledgeCommandAsync(
            ControllerCommand command,
            CancellationToken cancellationToken) => Task.FromResult(command);

        public Task CompleteCommandAsync(
            ControllerCommand command,
            RunnerCommandResult result,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> DownloadContentStringAsync(
            ContentReference contentRef,
            CancellationToken cancellationToken) => Task.FromResult(payload);

        public Task<byte[]> DownloadContentBytesAsync(
            ContentReference contentRef,
            CancellationToken cancellationToken) =>
            Task.FromResult(System.Text.Encoding.UTF8.GetBytes(payload));

        public Task<ContentReference> UploadContentAsync(
            string content,
            string contentType,
            CancellationToken cancellationToken)
        {
            var contentRef = Content(
                $"upload_{Uploads.Count}",
                contentType,
                content.Length);
            Uploads.Add(contentRef);
            return Task.FromResult(contentRef);
        }

        public Task<ContentReference> UploadContentAsync(
            byte[] bytes,
            string contentType,
            CancellationToken cancellationToken)
        {
            var contentRef = Content(
                $"upload_{Uploads.Count}",
                contentType,
                bytes.Length);
            Uploads.Add(contentRef);
            return Task.FromResult(contentRef);
        }

        public Task PublishEventAsync(
            ControllerEvent controllerEvent,
            CancellationToken cancellationToken)
        {
            PublishedEvents.Add(controllerEvent);
            return Task.CompletedTask;
        }
    }
}
