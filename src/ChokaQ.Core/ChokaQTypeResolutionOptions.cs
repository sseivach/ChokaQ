namespace ChokaQ.Core;

/// <summary>
/// Controls how Bus-mode job type identities are persisted and resolved.
/// </summary>
public sealed class ChokaQTypeResolutionOptions
{
    /// <summary>
    /// When true, Bus-mode enqueue requires every job type to be registered through
    /// a ChokaQJobProfile. This is the recommended production posture because the
    /// persisted type key becomes an explicit message-contract name.
    /// </summary>
    public bool RequireRegisteredJobTypes { get; set; }
}

