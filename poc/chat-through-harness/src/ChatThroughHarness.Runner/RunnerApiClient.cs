using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using ChatThroughHarness.Protocol.V1;
using Microsoft.Extensions.Options;

namespace ChatThroughHarness.Runner;

public interface IRunnerApiClient
{
    Task UpsertHeartbeatAsync(ControllerHeartbeat heartbeat, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<ControllerCommand>> PollCommandsAsync(CancellationToken cancellationToken);

    Task<ControllerCommand> AcknowledgeCommandAsync(
        ControllerCommand command,
        CancellationToken cancellationToken);

    Task CompleteCommandAsync(
        ControllerCommand command,
        RunnerCommandResult result,
        CancellationToken cancellationToken);

    Task<string> DownloadContentStringAsync(
        ContentReference contentRef,
        CancellationToken cancellationToken);

    Task<byte[]> DownloadContentBytesAsync(
        ContentReference contentRef,
        CancellationToken cancellationToken);

    Task<ContentReference> UploadContentAsync(
        string content,
        string contentType,
        CancellationToken cancellationToken);

    Task<ContentReference> UploadContentAsync(
        byte[] bytes,
        string contentType,
        CancellationToken cancellationToken);

    Task PublishEventAsync(ControllerEvent controllerEvent, CancellationToken cancellationToken);
}

public sealed class RunnerApiClient(HttpClient httpClient, IOptions<RunnerOptions> options) : IRunnerApiClient
{
    private readonly RunnerOptions _options = options.Value;

    public async Task UpsertHeartbeatAsync(
        ControllerHeartbeat heartbeat,
        CancellationToken cancellationToken)
    {
        var response = await httpClient.PostAsJsonAsync(
            "/api/v1/controllers/heartbeats",
            heartbeat,
            ControllerProtocolJson.Options,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyCollection<ControllerCommand>> PollCommandsAsync(
        CancellationToken cancellationToken)
    {
        var path =
            $"/api/v1/controllers/{Uri.EscapeDataString(_options.RunnerId)}/commands" +
            $"?wait={(int)_options.LongPollWait.TotalSeconds}s";
        var commands = await httpClient.GetFromJsonAsync<IReadOnlyCollection<ControllerCommand>>(
            path,
            ControllerProtocolJson.Options,
            cancellationToken);
        return commands ?? [];
    }

    public async Task<ControllerCommand> AcknowledgeCommandAsync(
        ControllerCommand command,
        CancellationToken cancellationToken)
    {
        var acknowledgement = new ControllerCommandAcknowledgement(
            MessageType: ControllerMessageTypes.CommandAcknowledgement,
            ProtocolVersion: ControllerProtocolVersions.Protocol,
            SchemaVersion: ControllerProtocolVersions.Schema,
            AcknowledgementId: $"ack_{Guid.NewGuid():N}",
            CommandId: command.CommandId,
            ControllerId: _options.RunnerId,
            Status: CommandAcknowledgementStatuses.Accepted,
            AcknowledgedAt: DateTimeOffset.UtcNow,
            Correlation: command.Correlation);
        var response = await httpClient.PostAsJsonAsync(
            $"/api/v1/controllers/{Uri.EscapeDataString(_options.RunnerId)}" +
            $"/commands/{Uri.EscapeDataString(command.CommandId)}/acknowledgements",
            acknowledgement,
            ControllerProtocolJson.Options,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var accepted =
            await response.Content.ReadFromJsonAsync<ControllerCommandAcknowledgement>(
                ControllerProtocolJson.Options,
                cancellationToken)
            ?? throw new InvalidOperationException(
                $"Acknowledgement response for {command.CommandId} was empty.");
        var lease = accepted.Lease
                    ?? throw new InvalidOperationException(
                        $"Acknowledgement response for {command.CommandId} had no lease.");
        return command with
        {
            Target = command.Target with { ControllerId = accepted.ControllerId },
            Execution = command.Execution with { Lease = lease }
        };
    }

    public async Task CompleteCommandAsync(
        ControllerCommand command,
        RunnerCommandResult result,
        CancellationToken cancellationToken)
    {
        var lease = command.Execution.Lease
                    ?? throw new InvalidOperationException(
                        $"Command {command.CommandId} cannot be completed without a lease.");
        var completion = new ControllerCommandCompletion(
            MessageType: ControllerMessageTypes.CommandCompletion,
            ProtocolVersion: ControllerProtocolVersions.Protocol,
            SchemaVersion: ControllerProtocolVersions.Schema,
            CompletionId: $"completion_{Guid.NewGuid():N}",
            CommandId: command.CommandId,
            ControllerId: _options.RunnerId,
            DeliveryStatus: result.DeliveryStatus,
            CompletedAt: DateTimeOffset.UtcNow,
            FencingToken: lease.FencingToken,
            Correlation: command.Correlation,
            ResultRef: result.ResultRef,
            Error: result.Error);
        var response = await httpClient.PostAsJsonAsync(
            $"/api/v1/controllers/{Uri.EscapeDataString(_options.RunnerId)}" +
            $"/commands/{Uri.EscapeDataString(command.CommandId)}/completion",
            completion,
            ControllerProtocolJson.Options,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<string> DownloadContentStringAsync(
        ContentReference contentRef,
        CancellationToken cancellationToken)
    {
        var bytes = await DownloadContentBytesAsync(contentRef, cancellationToken);
        return Encoding.UTF8.GetString(bytes);
    }

    public async Task<byte[]> DownloadContentBytesAsync(
        ContentReference contentRef,
        CancellationToken cancellationToken)
    {
        var contentId = ContentId(contentRef);
        var bytes = await httpClient.GetByteArrayAsync(
            $"/api/v1/controller-content/{contentId}",
            cancellationToken);
        var actualSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(actualSha, contentRef.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Downloaded content {contentRef.Uri} failed sha256 validation.");
        }

        if (bytes.LongLength != contentRef.Length)
        {
            throw new InvalidDataException(
                $"Downloaded content {contentRef.Uri} failed length validation.");
        }

        return bytes;
    }

    public async Task<ContentReference> UploadContentAsync(
        string content,
        string contentType,
        CancellationToken cancellationToken)
    {
        return await UploadContentAsync(
            Encoding.UTF8.GetBytes(content),
            contentType,
            cancellationToken);
    }

    public async Task<ContentReference> UploadContentAsync(
        byte[] bytes,
        string contentType,
        CancellationToken cancellationToken)
    {
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var response = await httpClient.PostAsJsonAsync(
            "/api/v1/controller-content",
            new ControllerContentUploadRequest(
                ContentType: contentType,
                Sha256: sha,
                Length: bytes.LongLength,
                ContentBase64: Convert.ToBase64String(bytes)),
            ControllerProtocolJson.Options,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var upload = await response.Content.ReadFromJsonAsync<ControllerContentUploadResponse>(
            ControllerProtocolJson.Options,
            cancellationToken);
        return upload?.ContentRef
               ?? throw new InvalidOperationException("Content upload response was empty.");
    }

    public async Task PublishEventAsync(
        ControllerEvent controllerEvent,
        CancellationToken cancellationToken)
    {
        var response = await httpClient.PostAsJsonAsync(
            $"/api/v1/controllers/{Uri.EscapeDataString(_options.RunnerId)}/events",
            controllerEvent,
            ControllerProtocolJson.Options,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static string ContentId(ContentReference contentRef)
    {
        return contentRef.Uri.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
    }
}
