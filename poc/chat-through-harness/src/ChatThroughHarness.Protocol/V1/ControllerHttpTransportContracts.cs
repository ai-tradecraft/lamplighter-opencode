using System.Text.Json.Serialization;

namespace ChatThroughHarness.Protocol.V1;

/// <summary>Uploads claim-check content used by controller protocol messages.</summary>
/// <param name="ContentType">The media type.</param>
/// <param name="Sha256">The SHA-256 digest.</param>
/// <param name="Length">The content length in bytes.</param>
/// <param name="ContentBase64">The base64-encoded content.</param>
public sealed record ControllerContentUploadRequest(
    [property: JsonPropertyName("content_type")] string ContentType,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("length")] long Length,
    [property: JsonPropertyName("content_base64")] string ContentBase64);

/// <summary>Returns the immutable reference created for uploaded controller content.</summary>
/// <param name="ContentRef">The stored content reference.</param>
public sealed record ControllerContentUploadResponse(
    [property: JsonPropertyName("content_ref")] ContentReference ContentRef);
