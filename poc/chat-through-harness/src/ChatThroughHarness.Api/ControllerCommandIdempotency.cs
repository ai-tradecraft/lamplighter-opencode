using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatThroughHarness.Protocol.V1;

internal static class ControllerCommandIdempotency
{
    internal static string Scope(ControllerCommand command)
    {
        return string.Join(
            '\n',
            command.Target.ControllerId ?? "*",
            command.CommandType,
            command.IdempotencyKey);
    }

    internal static string Fingerprint(ControllerCommand command)
    {
        var authorizationLifetime = command.AuthorizationContext.ExpiresAt
                                    - command.AuthorizationContext.IssuedAt;
        var normalized = JsonSerializer.SerializeToElement(
            new
            {
                command.MessageType,
                command.ProtocolVersion,
                command.SchemaVersion,
                command.CommandType,
                target = command.Target with { ControllerId = null },
                correlation = command.Correlation with
                {
                    CommandId = null,
                    CausationId = null,
                    TraceId = null,
                    SpanId = null
                },
                authorization = new
                {
                    command.AuthorizationContext.SubjectRef,
                    command.AuthorizationContext.GrantRef,
                    lifetime_seconds = authorizationLifetime?.TotalSeconds,
                    command.AuthorizationContext.TenantId
                },
                execution = new
                {
                    deadline_seconds =
                        (command.Execution.Deadline - command.IssuedAt).TotalSeconds,
                    command.Execution.HeartbeatInterval
                },
                available_after_seconds = command.AvailableAt is null
                    ? (double?)null
                    : (command.AvailableAt.Value - command.IssuedAt).TotalSeconds,
                payload_ref = command.PayloadRef is null
                    ? null
                    : new
                    {
                        command.PayloadRef.Sha256,
                        command.PayloadRef.ContentType,
                        command.PayloadRef.Length,
                        command.PayloadRef.SchemaRef,
                        command.PayloadRef.Encryption
                    },
                command.Payload,
                command.Extensions
            },
            ControllerProtocolJson.Options);
        return SemanticHash(normalized);
    }

    internal static string SemanticHash<T>(T value)
    {
        var element = value is JsonElement jsonElement
            ? jsonElement
            : JsonSerializer.SerializeToElement(value, ControllerProtocolJson.Options);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, element);
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray()))
            .ToLowerInvariant();
    }

    internal static string PathToken(string scope)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))
            .ToLowerInvariant();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported JSON kind {element.ValueKind} in command fingerprint.");
        }
    }
}

public sealed record ControllerCommandIdempotencyRecord(
    string Scope,
    string Fingerprint,
    ControllerCommand Command,
    DateTimeOffset RecordedAt);

public enum ControllerCommandEnqueueStatus
{
    Accepted,
    Replayed,
    Conflict
}

public sealed record ControllerCommandEnqueueResult(
    ControllerCommandEnqueueStatus Status,
    ControllerCommand? Command,
    ControllerCommandCompletion? Completion,
    string? Message)
{
    public static ControllerCommandEnqueueResult Accepted(
        ControllerCommand command) =>
        new(ControllerCommandEnqueueStatus.Accepted, command, null, null);

    public static ControllerCommandEnqueueResult Replayed(
        ControllerCommand command,
        ControllerCommandCompletion? completion) =>
        new(ControllerCommandEnqueueStatus.Replayed, command, completion, null);

    public static ControllerCommandEnqueueResult Conflict(string message) =>
        new(ControllerCommandEnqueueStatus.Conflict, null, null, message);
}
