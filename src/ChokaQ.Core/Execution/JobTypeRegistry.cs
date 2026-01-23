using System.Collections.Concurrent;

namespace ChokaQ.Core.Execution;

/// <summary>
/// Maintains a bidirectional mapping between string Keys (stored in DB) and C# Types.
/// Populated at startup via Profiles.
/// </summary>
public class JobTypeRegistry
{
    // Key: "email_v1" -> Value: typeof(EmailJobDto)
    private readonly ConcurrentDictionary<string, Type> _keyToTypeMap = new(StringComparer.OrdinalIgnoreCase);

    // Key: typeof(EmailJobDto) -> Value: "email_v1"
    private readonly ConcurrentDictionary<Type, string> _typeToKeyMap = new();

    /// <summary>
    /// Registers a job type with a specific key.
    /// </summary>
    public void Register(string key, Type jobType)
    {
        if (!_keyToTypeMap.TryAdd(key, jobType))
        {
            throw new InvalidOperationException($"Duplicate Job Key detected: '{key}'. Keys must be unique across all profiles.");
        }

        // We also allow reverse lookup. 
        // If one DTO is mapped to multiple keys (rare), the first one wins for reverse lookup.
        _typeToKeyMap.TryAdd(jobType, key);
    }

    /// <summary>
    /// Resolves the C# Type for a given job key (used by Worker/Dispatcher).
    /// </summary>
    public Type? GetTypeByKey(string key)
    {
        if (_keyToTypeMap.TryGetValue(key, out var type))
        {
            return type;
        }
        return null;
    }

    /// <summary>
    /// Resolves the Job Key for a given C# Type (used by Queue/Enqueuer).
    /// </summary>
    public string? GetKeyByType(Type type)
    {
        if (_typeToKeyMap.TryGetValue(type, out var key))
        {
            return key;
        }
        return null;
    }

    public IEnumerable<string> GetAllKeys() => _keyToTypeMap.Keys;
}