using System.Text;

namespace ChokaQ.Core.Validation;

internal static class ChokaQEnvelopeLimits
{
    public const int MaxQueueLength = 255;
    public const int MaxTypeKeyLength = 255;
    public const int MaxCreatedByLength = 100;
    public const int MaxTagsLength = 1000;
    public const int MaxIdempotencyKeyLength = 255;

    public static void ValidateEnvelope(string queue, string? createdBy, string? tags)
    {
        if (string.IsNullOrWhiteSpace(queue))
            throw new ArgumentException("Queue name cannot be null or empty.", nameof(queue));

        if (queue.Length > MaxQueueLength)
            throw new ArgumentException($"Queue name '{queue}' exceeds maximum length of {MaxQueueLength} characters.", nameof(queue));

        if (createdBy != null && createdBy.Length > MaxCreatedByLength)
            throw new ArgumentException($"CreatedBy '{createdBy}' exceeds maximum length of {MaxCreatedByLength} characters.", nameof(createdBy));

        if (tags != null && tags.Length > MaxTagsLength)
            throw new ArgumentException($"Tags exceed maximum length of {MaxTagsLength} characters.", nameof(tags));
    }

    public static void ValidateTypeKey(string typeKey)
    {
        if (string.IsNullOrWhiteSpace(typeKey))
            throw new InvalidOperationException("Job Type Key cannot be null or empty.");

        if (typeKey.Length > MaxTypeKeyLength)
            throw new InvalidOperationException($"Job Type Key '{typeKey}' exceeds maximum length of {MaxTypeKeyLength} characters.");
    }

    public static string? NormalizeIdempotencyKey(string? key, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        key = key.Trim();
        if (key.Length > MaxIdempotencyKeyLength)
        {
            throw new ArgumentException(
                $"IdempotencyKey exceeds maximum length of {MaxIdempotencyKeyLength} characters.",
                parameterName);
        }

        return key;
    }

    public static void ValidatePayloadSize(string payload, int maxPayloadBytes)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var byteCount = Encoding.UTF8.GetByteCount(payload);
        if (byteCount > maxPayloadBytes)
        {
            throw new InvalidOperationException(
                $"Serialized job payload is {byteCount} bytes and exceeds the configured maximum of {maxPayloadBytes} bytes.");
        }
    }
}
