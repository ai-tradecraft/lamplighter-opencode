using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using ChatThroughHarness.Protocol;
using Microsoft.Extensions.Options;

namespace ChatThroughHarness.Runner;

public interface IRunnerApiClient
{
    Task UpsertHeartbeatAsync(RunnerHeartbeat heartbeat, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<RunnerCommandEnvelope>> PollCommandsAsync(CancellationToken cancellationToken);
    Task<RunnerCommandEnvelope> ClaimCommandAsync(RunnerCommandEnvelope command, CancellationToken cancellationToken);
    Task CompleteCommandAsync(RunnerCommandEnvelope command, RunnerCommandResult result, CancellationToken cancellationToken);
    Task<string> DownloadContentStringAsync(ClaimCheckContentRef contentRef, CancellationToken cancellationToken);
    Task<byte[]> DownloadContentBytesAsync(ClaimCheckContentRef contentRef, CancellationToken cancellationToken);
    Task<ClaimCheckContentRef> UploadContentAsync(string content, string contentType, CancellationToken cancellationToken);
    Task<ClaimCheckContentRef> UploadContentAsync(byte[] bytes, string contentType, CancellationToken cancellationToken);
    Task PublishEventAsync(RunnerEventEnvelope runnerEvent, CancellationToken cancellationToken);
}

public sealed class RunnerApiClient(HttpClient httpClient, IOptions<RunnerOptions> options) : IRunnerApiClient
{
    private readonly RunnerOptions _options = options.Value;

    public async Task UpsertHeartbeatAsync(RunnerHeartbeat heartbeat, CancellationToken cancellationToken)
    {
        var response = await httpClient.PostAsJsonAsync(
            "/api/runner/heartbeat",
            heartbeat,
            RunnerProtocolJson.Options,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

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

    public async Task<string> DownloadContentStringAsync(
        ClaimCheckContentRef contentRef,
        CancellationToken cancellationToken)
    {
        var bytes = await DownloadContentBytesAsync(contentRef, cancellationToken);
        return Encoding.UTF8.GetString(bytes);
    }

    public async Task<byte[]> DownloadContentBytesAsync(
        ClaimCheckContentRef contentRef,
        CancellationToken cancellationToken)
    {
        var contentId = ContentId(contentRef);
        var bytes = await httpClient.GetByteArrayAsync($"/api/runner/content/{contentId}", cancellationToken);
        var actualSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(actualSha, contentRef.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Downloaded content {contentRef.Uri} failed sha256 validation.");
        }

        if (bytes.LongLength != contentRef.Length)
        {
            throw new InvalidDataException($"Downloaded content {contentRef.Uri} failed length validation.");
        }

        return bytes;
    }

    public async Task<ClaimCheckContentRef> UploadContentAsync(
        string content,
        string contentType,
        CancellationToken cancellationToken)
    {
        return await UploadContentAsync(Encoding.UTF8.GetBytes(content), contentType, cancellationToken);
    }

    public async Task<ClaimCheckContentRef> UploadContentAsync(
        byte[] bytes,
        string contentType,
        CancellationToken cancellationToken)
    {
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var response = await httpClient.PostAsJsonAsync(
            "/api/runner/content",
            new ClaimCheckContentUploadRequest(
                ContentType: contentType,
                Sha256: sha,
                Length: bytes.LongLength,
                ContentBase64: Convert.ToBase64String(bytes)),
            RunnerProtocolJson.Options,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var upload = await response.Content.ReadFromJsonAsync<ClaimCheckContentUploadResponse>(
            RunnerProtocolJson.Options,
            cancellationToken);
        return upload?.ContentRef ?? throw new InvalidOperationException("Content upload response was empty.");
    }

    public async Task PublishEventAsync(
        RunnerEventEnvelope runnerEvent,
        CancellationToken cancellationToken)
    {
        var response = await httpClient.PostAsJsonAsync(
            "/api/runner/events",
            runnerEvent,
            RunnerProtocolJson.Options,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static string ContentId(ClaimCheckContentRef contentRef)
    {
        return contentRef.Uri.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
    }
}
