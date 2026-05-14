using ChokaQ.Abstractions.Jobs;

namespace ChokaQ.Abstractions.Serialization;

/// <summary>
/// Serializes and deserializes Bus-mode job payloads.
/// </summary>
public interface IChokaQJobSerializer
{
    string Serialize(IChokaQJob job, Type jobType);

    IChokaQJob? Deserialize(string payload, Type jobType);

    string SerializeCompletionMarker(string jobId, DateTimeOffset completedAt);
}

