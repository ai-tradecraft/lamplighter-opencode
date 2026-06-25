using System.Net.Http.Json;
using ChatThroughHarness.Protocol;
using Microsoft.Extensions.Options;

namespace ChatThroughHarness.Runner;

public sealed class RunnerApiClient(HttpClient httpClient, IOptions<RunnerOptions> options)
{
    private readonly RunnerOptions _options = options.Value;

    public async Task<IReadOnlyCollection<RunnerCommandEnvelope>> PollCommandsAsync(CancellationToken cancellationToken)
    {
        var path = $"/api/runner/commands?runnerId={Uri.EscapeDataString(_options.RunnerId)}&wait={(int)_options.LongPollWait.TotalSeconds}s";
        var commands = await httpClient.GetFromJsonAsync<IReadOnlyCollection<RunnerCommandEnvelope>>(
            path,
            RunnerProtocolJson.Options,
            cancellationToken);
        return commands ?? [];
    }

    public async Task<RunnerCommandEnvelope> ClaimCommandAsync(
        RunnerCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        var response = await httpClient.PostAsJsonAsync(
            $"/api/runner/commands/{command.Id}/claim",
            new ClaimRunnerCommandRequest(_options.RunnerId, _options.LeaseSeconds),
            RunnerProtocolJson.Options,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RunnerCommandEnvelope>(RunnerProtocolJson.Options, cancellationToken)
            ?? throw new InvalidOperationException($"Claim response for {command.Id} was empty.");
    }

    public async Task CompleteCommandAsync(
        RunnerCommandEnvelope command,
        RunnerCommandResult result,
        CancellationToken cancellationToken)
    {
        if (command.Lease is null)
        {
            throw new InvalidOperationException($"Command {command.Id} cannot be completed without a lease.");
        }

        var response = await httpClient.PostAsJsonAsync(
            $"/api/runner/commands/{command.Id}/complete",
            new CompleteRunnerCommandRequest(
                RunnerId: _options.RunnerId,
                LeaseId: command.Lease.LeaseId,
                Status: result.Status,
                CompletedAt: DateTimeOffset.UtcNow,
                ResultRef: result.ResultRef,
                Failure: result.Failure),
            RunnerProtocolJson.Options,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
