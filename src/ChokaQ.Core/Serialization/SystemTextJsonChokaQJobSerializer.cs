using ChokaQ.Abstractions.Jobs;
using ChokaQ.Abstractions.Serialization;
using System.Text.Json;

namespace ChokaQ.Core.Serialization;

public sealed class SystemTextJsonChokaQJobSerializer : IChokaQJobSerializer
{
    private readonly JsonSerializerOptions _options;

    public SystemTextJsonChokaQJobSerializer(ChokaQSerializationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = new JsonSerializerOptions(options.JsonSerializerOptions);
    }

    public string Serialize(IChokaQJob job, Type jobType)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(jobType);

        return JsonSerializer.Serialize(job, jobType, _options);
    }

    public IChokaQJob? Deserialize(string payload, Type jobType)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(jobType);

        return JsonSerializer.Deserialize(payload, jobType, _options) as IChokaQJob;
    }

    public string SerializeCompletionMarker(string jobId, DateTimeOffset completedAt)
    {
        var marker = new CompletionMarker(completedAt, jobId);
        return JsonSerializer.Serialize(marker, _options);
    }

    private sealed record CompletionMarker(DateTimeOffset CompletedAt, string JobId);
}

