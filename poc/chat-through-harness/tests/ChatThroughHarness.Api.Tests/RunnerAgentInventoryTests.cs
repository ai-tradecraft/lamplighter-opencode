namespace ChatThroughHarness.Api.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ChatThroughHarness.Runner;
using Xunit;

public sealed class RunnerAgentInventoryTests
{
    [Fact]
    public async Task ManagedAgentReportsMultipleChildSessions()
    {
        var root = NewRuntimeRoot();
        var agentRoot = Path.Combine(root, "agents", "agent_1");
        var runtimeRoot = Path.Combine(agentRoot, "runtime");
        Directory.CreateDirectory(Path.Combine(agentRoot, "workspace"));
        Directory.CreateDirectory(runtimeRoot);
        File.WriteAllText(
            Path.Combine(agentRoot, "agent.json"),
            JsonSerializer.Serialize(new { agent_id = "agent_1", status = "ready" }));
        File.WriteAllText(
            Path.Combine(runtimeRoot, "opencode-server.json"),
            JsonSerializer.Serialize(new
            {
                status = "ready",
                endpoint = "http://127.0.0.1:4097",
                pid = Environment.ProcessId,
                auth = new { username = "opencode", password = "secret" }
            }));
        foreach (var sessionId in new[] { "session_1", "session_2" })
        {
            var sessionRoot = Path.Combine(runtimeRoot, "sessions", sessionId);
            Directory.CreateDirectory(sessionRoot);
            File.WriteAllText(
                Path.Combine(sessionRoot, "session.json"),
                JsonSerializer.Serialize(new
                {
                    session_id = sessionId,
                    agent_id = "agent_1",
                    status = "ready",
                    opencode_session_id = $"opencode_{sessionId}"
                }));
        }

        var agents = await RunnerAgentInventory.ScanAsync(
            root,
            DateTimeOffset.UtcNow,
            new FakeHealthProbe("ready"),
            CancellationToken.None);

        var agent = Assert.Single(agents);
        Assert.Equal("agent_1", agent.AgentId);
        Assert.Equal(Path.Combine(agentRoot, "workspace"), agent.WorkspacePath);
        Assert.Collection(
            agent.Sessions!,
            session => Assert.Equal("session_1", session.SessionId),
            session => Assert.Equal("session_2", session.SessionId));
    }

    [Fact]
    public async Task ReadyAgentBecomesUnreachableWhenHealthProbeFails()
    {
        var root = NewRuntimeRoot();
        var sessionRoot = CreateSession(root, "session_1", "ready");
        WriteServerMetadata(sessionRoot, "ready");
        var probe = new FakeHealthProbe("unreachable");

        var agents = await RunnerAgentInventory.ScanAsync(
            root,
            DateTimeOffset.UtcNow,
            probe,
            CancellationToken.None);

        var agent = Assert.Single(agents);
        Assert.Equal("unreachable", agent.Status);
        Assert.Equal(1, probe.CallCount);
    }

    [Fact]
    public async Task TerminalSessionStatusTakesPrecedenceOverServerMetadata()
    {
        var root = NewRuntimeRoot();
        var sessionRoot = CreateSession(root, "session_1", "cancelled");
        WriteServerMetadata(sessionRoot, "ready");
        var probe = new FakeHealthProbe("ready");

        var agents = await RunnerAgentInventory.ScanAsync(
            root,
            DateTimeOffset.UtcNow,
            probe,
            CancellationToken.None);

        var agent = Assert.Single(agents);
        Assert.Equal("cancelled", agent.Status);
        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public async Task LegacyReadySessionWithEndedAtIsRecoveredAsCancelled()
    {
        var root = NewRuntimeRoot();
        var sessionRoot = CreateSession(root, "session_1", "ready", DateTimeOffset.UtcNow);
        WriteServerMetadata(sessionRoot, "ready");
        var probe = new FakeHealthProbe("ready");

        var agents = await RunnerAgentInventory.ScanAsync(
            root,
            DateTimeOffset.UtcNow,
            probe,
            CancellationToken.None);

        var agent = Assert.Single(agents);
        Assert.Equal("cancelled", agent.Status);
        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public async Task HealthProbeRequiresLiveProcessAndAuthenticatedHealthyResponse()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"healthy":true}""", Encoding.UTF8, "application/json")
        });
        var probe = new OpenCodeHealthProbe(new HttpClient(handler));

        var status = await probe.GetStatusAsync(
            "http://127.0.0.1:4097",
            Environment.ProcessId,
            "opencode",
            "secret",
            CancellationToken.None);

        Assert.Equal("ready", status);
        Assert.Equal("http://127.0.0.1:4097/global/health", handler.RequestUri);
        Assert.Equal(
            new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:secret"))),
            handler.Authorization);
    }

    [Fact]
    public async Task HealthProbeReportsStoppedBeforeCallingEndpointWhenProcessIsGone()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var probe = new OpenCodeHealthProbe(new HttpClient(handler));

        var status = await probe.GetStatusAsync(
            "http://127.0.0.1:4097",
            int.MaxValue,
            "opencode",
            "secret",
            CancellationToken.None);

        Assert.Equal("stopped", status);
        Assert.Equal(0, handler.CallCount);
    }

    private static string CreateSession(
        string root,
        string sessionId,
        string status,
        DateTimeOffset? endedAt = null)
    {
        var sessionRoot = Path.Combine(root, "sessions", sessionId);
        Directory.CreateDirectory(sessionRoot);
        File.WriteAllText(
            Path.Combine(sessionRoot, "session.json"),
            JsonSerializer.Serialize(new
            {
                id = sessionId,
                status,
                endedAt,
                workspacePath = Path.Combine(sessionRoot, "workspace")
            }));
        return sessionRoot;
    }

    private static void WriteServerMetadata(string sessionRoot, string status)
    {
        File.WriteAllText(
            Path.Combine(sessionRoot, "opencode-server.json"),
            JsonSerializer.Serialize(new
            {
                status,
                endpoint = "http://127.0.0.1:4097",
                pid = 123,
                auth = new { username = "opencode", password = "secret" }
            }));
    }

    private static string NewRuntimeRoot()
    {
        return Path.Combine(Path.GetTempPath(), Ids.New("runner_inventory"));
    }

    private sealed class FakeHealthProbe(string status) : IOpenCodeHealthProbe
    {
        public int CallCount { get; private set; }

        public Task<string> GetStatusAsync(
            string endpoint,
            int? processId,
            string? username,
            string? password,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(status);
        }
    }

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public string? RequestUri { get; private set; }
        public AuthenticationHeaderValue? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            RequestUri = request.RequestUri?.ToString();
            Authorization = request.Headers.Authorization;
            return Task.FromResult(response);
        }
    }
}
