using System.Collections.Concurrent;

namespace ChokaQ.Core.Execution;

/// <summary>
/// Maintains a bidirectional mapping between string Keys (stored in DB) and C# Types.
/// Populated at startup via Profiles.
/// </summary>
internal class JobTypeRegistry
{
    // Key: "email.send.v1" -> Value: typeof(EmailJobDto)
    private readonly ConcurrentDictionary<string, Type> _keyToTypeMap = new(StringComparer.OrdinalIgnoreCase);

    // Key: typeof(EmailJobDto) -> Value: "email.send.v1"
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

    /// <summary>
    /// Returns the persisted dispatch key for a runtime job type.
    /// Registered profile keys are preferred. If compatibility fallback is allowed,
    /// the fallback is assembly-qualified so later resolution does not require
    /// scanning all loaded assemblies or matching unsafe CLR short names.
    /// </summary>
    public string GetPersistedTypeKey(Type type, bool requireRegisteredJobTypes = false)
    {
        if (_typeToKeyMap.TryGetValue(type, out var key))
        {
            return key;
        }

        if (requireRegisteredJobTypes)
        {
            throw new InvalidOperationException(
                $"Job type '{type.FullName}' is not registered. Register it in a ChokaQJobProfile or disable strict type registration.");
        }

        return type.AssemblyQualifiedName
            ?? throw new InvalidOperationException($"Job type '{type.FullName}' does not have an assembly-qualified name.");
    }

    /// <summary>
    /// Resolves a persisted dispatch key without runtime assembly scans.
    /// </summary>
    public Type? ResolvePersistedType(string key)
    {
        if (_keyToTypeMap.TryGetValue(key, out var registeredType))
        {
            return registeredType;
        }

        return Type.GetType(key, throwOnError: false, ignoreCase: false);
    }

    public IEnumerable<string> GetAllKeys() => _keyToTypeMap.Keys;
}
