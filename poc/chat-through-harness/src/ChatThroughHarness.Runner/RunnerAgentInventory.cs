using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ChatThroughHarness.Runner;

public sealed record RunnerAgentInventoryItem(
    string AgentSessionId,
    string Status,
    string RuntimePath,
    string WorkspacePath,
    string? OpenCodeEndpoint,
    int? OpenCodePid,
    DateTimeOffset ObservedAt,
    string? AgentId = null,
    IReadOnlyList<RunnerAgentSessionInventoryItem>? Sessions = null);

public sealed record RunnerAgentSessionInventoryItem(
    string SessionId,
    string Status,
    string RuntimePath,
    string? OpenCodeSessionId,
    DateTimeOffset ObservedAt);

public static class RunnerAgentInventory
{
    private static readonly HashSet<string> TerminalStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "cancelled", "failed" };

    public static async Task<IReadOnlyList<RunnerAgentInventoryItem>> ScanAsync(
        string runtimeRoot,
        DateTimeOffset observedAt,
        IOpenCodeHealthProbe healthProbe,
        CancellationToken cancellationToken)
    {
        var agentsRoot = Path.Combine(runtimeRoot, "agents");
        if (Directory.Exists(agentsRoot))
        {
            var managedAgents = await Task.WhenAll(
                Directory.EnumerateDirectories(agentsRoot)
                    .Select(path => ReadManagedAgentAsync(path, observedAt, healthProbe, cancellationToken)));
            return managedAgents
                .Where(item => item is not null)
                .Select(item => item!)
                .OrderBy(item => item.AgentId, StringComparer.Ordinal)
                .ToArray();
        }

        var sessionsRoot = Path.Combine(runtimeRoot, "sessions");
        if (!Directory.Exists(sessionsRoot))
        {
            return [];
        }

        var agents = await Task.WhenAll(
            Directory.EnumerateDirectories(sessionsRoot)
                .Select(path => ReadAgentAsync(path, observedAt, healthProbe, cancellationToken)));
        return agents
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderBy(item => item.AgentSessionId, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<RunnerAgentInventoryItem?> ReadManagedAgentAsync(
        string agentRoot,
        DateTimeOffset observedAt,
        IOpenCodeHealthProbe healthProbe,
        CancellationToken cancellationToken)
    {
        var metadataPath = Path.Combine(agentRoot, "agent.json");
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        using var metadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
        var agentId = ReadString(metadata.RootElement, "agent_id") ?? Path.GetFileName(agentRoot);
        var status = ReadString(metadata.RootElement, "status") ?? "allocated";
        var runtimePath = Path.Combine(agentRoot, "runtime");
        var workspacePath = Path.Combine(agentRoot, "workspace");
        string? endpoint = null;
        int? pid = null;
        string? username = null;
        string? password = null;

        var serverPath = Path.Combine(runtimePath, "opencode-server.json");
        if (File.Exists(serverPath))
        {
            using var server = JsonDocument.Parse(File.ReadAllText(serverPath));
            if (!TerminalStatuses.Contains(status))
            {
                status = ReadString(server.RootElement, "status") ?? status;
            }
            endpoint = ReadString(server.RootElement, "endpoint");
            if (server.RootElement.TryGetProperty("pid", out var pidElement)
                && pidElement.ValueKind == JsonValueKind.Number)
            {
                pid = pidElement.GetInt32();
            }
            if (server.RootElement.TryGetProperty("auth", out var auth)
                && auth.ValueKind == JsonValueKind.Object)
            {
                username = ReadString(auth, "username");
                password = ReadString(auth, "password");
            }
        }

        if (status.Equals("ready", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(endpoint))
        {
            status = await healthProbe.GetStatusAsync(endpoint, pid, username, password, cancellationToken);
        }

        var sessions = ReadManagedSessions(runtimePath, observedAt);
        return new RunnerAgentInventoryItem(
            AgentSessionId: sessions.FirstOrDefault()?.SessionId ?? agentId,
            Status: status,
            RuntimePath: runtimePath,
            WorkspacePath: workspacePath,
            OpenCodeEndpoint: endpoint,
            OpenCodePid: pid,
            ObservedAt: observedAt,
            AgentId: agentId,
            Sessions: sessions);
    }

    private static IReadOnlyList<RunnerAgentSessionInventoryItem> ReadManagedSessions(
        string runtimePath,
        DateTimeOffset observedAt)
    {
        var sessionsRoot = Path.Combine(runtimePath, "sessions");
        if (!Directory.Exists(sessionsRoot))
        {
            return [];
        }

        return Directory.EnumerateDirectories(sessionsRoot)
            .Select(path =>
            {
                var sessionId = Path.GetFileName(path);
                var status = "allocated";
                string? openCodeSessionId = null;
                var metadataPath = Path.Combine(path, "session.json");
                if (File.Exists(metadataPath))
                {
                    using var metadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
                    sessionId = ReadString(metadata.RootElement, "session_id") ?? sessionId;
                    status = ReadString(metadata.RootElement, "status") ?? status;
                    openCodeSessionId = ReadString(metadata.RootElement, "opencode_session_id");
                }
                return new RunnerAgentSessionInventoryItem(
                    sessionId,
                    status,
                    path,
                    openCodeSessionId,
                    observedAt);
            })
            .OrderBy(session => session.SessionId, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<RunnerAgentInventoryItem?> ReadAgentAsync(
        string sessionRoot,
        DateTimeOffset observedAt,
        IOpenCodeHealthProbe healthProbe,
        CancellationToken cancellationToken)
    {
        var sessionId = Path.GetFileName(sessionRoot);
        var workspacePath = Path.Combine(sessionRoot, "workspace");
        var status = "allocated";
        string? endpoint = null;
        int? pid = null;
        string? username = null;
        string? password = null;

        var sessionPath = Path.Combine(sessionRoot, "session.json");
        if (File.Exists(sessionPath))
        {
            using var session = JsonDocument.Parse(File.ReadAllText(sessionPath));
            if (session.RootElement.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
            {
                sessionId = idElement.GetString() ?? sessionId;
            }
            else if (session.RootElement.TryGetProperty("agent_session_id", out var agentSessionIdElement)
                && agentSessionIdElement.ValueKind == JsonValueKind.String)
            {
                sessionId = agentSessionIdElement.GetString() ?? sessionId;
            }

            if (session.RootElement.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.String)
            {
                status = statusElement.GetString() ?? status;
            }

            if (!TerminalStatuses.Contains(status)
                && session.RootElement.TryGetProperty("endedAt", out var endedAtElement)
                && endedAtElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                status = "cancelled";
            }

            if (session.RootElement.TryGetProperty("workspacePath", out var workspacePathElement)
                && workspacePathElement.ValueKind == JsonValueKind.String)
            {
                workspacePath = workspacePathElement.GetString() ?? workspacePath;
            }
        }

        var serverPath = Path.Combine(sessionRoot, "opencode-server.json");
        if (File.Exists(serverPath))
        {
            using var server = JsonDocument.Parse(File.ReadAllText(serverPath));
            if (!TerminalStatuses.Contains(status)
                && server.RootElement.TryGetProperty("status", out var serverStatusElement)
                && serverStatusElement.ValueKind == JsonValueKind.String)
            {
                status = serverStatusElement.GetString() ?? status;
            }

            if (server.RootElement.TryGetProperty("endpoint", out var endpointElement) && endpointElement.ValueKind == JsonValueKind.String)
            {
                endpoint = endpointElement.GetString();
            }

            if (server.RootElement.TryGetProperty("pid", out var pidElement) && pidElement.ValueKind == JsonValueKind.Number)
            {
                pid = pidElement.GetInt32();
            }

            if (server.RootElement.TryGetProperty("auth", out var authElement)
                && authElement.ValueKind == JsonValueKind.Object)
            {
                username = ReadString(authElement, "username");
                password = ReadString(authElement, "password");
            }
        }

        if (status.Equals("ready", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(endpoint))
        {
            status = await healthProbe.GetStatusAsync(
                endpoint,
                pid,
                username,
                password,
                cancellationToken);
        }

        return string.IsNullOrWhiteSpace(sessionId)
            ? null
            : new RunnerAgentInventoryItem(
                AgentSessionId: sessionId,
                Status: status,
                RuntimePath: sessionRoot,
                WorkspacePath: workspacePath,
                OpenCodeEndpoint: endpoint,
                OpenCodePid: pid,
                ObservedAt: observedAt);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}

public interface IOpenCodeHealthProbe
{
    Task<string> GetStatusAsync(
        string endpoint,
        int? processId,
        string? username,
        string? password,
        CancellationToken cancellationToken);
}

public sealed class OpenCodeHealthProbe(HttpClient httpClient) : IOpenCodeHealthProbe
{
    public async Task<string> GetStatusAsync(
        string endpoint,
        int? processId,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        if (!IsProcessRunning(processId))
        {
            return "stopped";
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{endpoint.TrimEnd('/')}/global/health");
        if (!string.IsNullOrEmpty(username) && password is not null)
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var response = await httpClient.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return "unreachable";
            }

            await using var content = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var payload = await JsonDocument.ParseAsync(content, cancellationToken: timeout.Token);
            return payload.RootElement.TryGetProperty("healthy", out var healthy)
                && healthy.ValueKind == JsonValueKind.True
                    ? "ready"
                    : "unreachable";
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "unreachable";
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return "unreachable";
        }
    }

    private static bool IsProcessRunning(int? processId)
    {
        if (processId is null)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId.Value);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
