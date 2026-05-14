using System.Text.Json;

namespace ChokaQ.Core;

/// <summary>
/// Payload serialization and size limits for persisted jobs.
/// </summary>
public sealed class ChokaQSerializationOptions
{
    /// <summary>
    /// Maximum UTF-8 byte size for a serialized job payload accepted by enqueue.
    /// </summary>
    public int MaxPayloadBytes { get; set; } = 1_000_000;

    /// <summary>
    /// Shared System.Text.Json options used by the default ChokaQ serializer.
    /// This property is intended for code configuration; appsettings should use
    /// explicit ChokaQ options such as MaxPayloadBytes.
    /// </summary>
    public JsonSerializerOptions JsonSerializerOptions { get; set; } = new();
}
