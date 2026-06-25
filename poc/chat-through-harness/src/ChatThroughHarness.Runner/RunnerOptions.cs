namespace ChatThroughHarness.Runner;

public sealed record RunnerOptions
{
    public string RunnerId { get; init; } = $"runner_{Environment.MachineName.ToLowerInvariant()}";
    public Uri OrchestratorBaseUri { get; init; } = new("http://127.0.0.1:5087");
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan LongPollWait { get; init; } = TimeSpan.FromSeconds(30);
}
