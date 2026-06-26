using System.Text.Json;
using ChatThroughHarness.Protocol;

namespace ChatThroughHarness.Runner;

public static class RunnerAgentInventory
{
    public static IReadOnlyList<RunnerAgentInventoryItem> Scan(string runtimeRoot, DateTimeOffset observedAt)
    {
        var sessionsRoot = Path.Combine(runtimeRoot, "sessions");
        if (!Directory.Exists(sessionsRoot))
        {
            return [];
        }

        return Directory.EnumerateDirectories(sessionsRoot)
            .Select(path => ReadAgent(path, observedAt))
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderBy(item => item.AgentSessionId, StringComparer.Ordinal)
            .ToArray();
    }

    private static RunnerAgentInventoryItem? ReadAgent(string sessionRoot, DateTimeOffset observedAt)
    {
        var sessionId = Path.GetFileName(sessionRoot);
        var workspacePath = Path.Combine(sessionRoot, "workspace");
        var status = "allocated";
        string? endpoint = null;
        int? pid = null;

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
            if (server.RootElement.TryGetProperty("status", out var serverStatusElement) && serverStatusElement.ValueKind == JsonValueKind.String)
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
}
