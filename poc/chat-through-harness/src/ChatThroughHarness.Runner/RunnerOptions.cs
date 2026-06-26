namespace ChatThroughHarness.Runner;

public sealed record RunnerOptions
{
    public string RunnerId { get; init; } = $"runner_{Environment.MachineName.ToLowerInvariant()}";
    public Uri OrchestratorBaseUri { get; init; } = new("http://127.0.0.1:5087");
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan LongPollWait { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(10);
    public int LeaseSeconds { get; init; } = 60;
    public TimeSpan RetryBackoff { get; init; } = TimeSpan.FromSeconds(5);
    public string RuntimeRoot { get; init; } = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.agent-runtime"));
    public string? HarnessRepoRoot { get; init; }
}
