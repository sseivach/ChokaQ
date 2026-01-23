using ChokaQ.Abstractions;
using System;
using System.Collections.Generic;

namespace ChokaQ.Core;

/// <summary>
/// Configuration options for the ChokaQ engine.
/// Supports "Pipe" mode (raw) or "Bus" mode (via explicit Profiles).
/// </summary>
public class ChokaQOptions
{
    internal bool IsPipeMode { get; private set; } = false;
    internal Type? PipeHandlerType { get; private set; }

    // List of profile types to instantiate and register
    internal List<Type> ProfileTypes { get; private set; } = new();

    /// <summary>
    /// Enables "Bus" mode and registers a mapping profile.
    /// </summary>
    /// <typeparam name="TProfile">The profile class inheriting from ChokaQJobProfile.</typeparam>
    public void AddProfile<TProfile>() where TProfile : ChokaQJobProfile
    {
        IsPipeMode = false; // Adding a profile implies Bus mode
        ProfileTypes.Add(typeof(TProfile));
    }

    /// <summary>
    /// Enables "Pipe" mode.
    /// All jobs are routed to the specified global handler as raw JSON.
    /// </summary>
    /// <typeparam name="THandler">The implementation of the global pipe handler.</typeparam>
    public void UsePipe<THandler>() where THandler : class, IChokaQPipeHandler
    {
        IsPipeMode = true;
        PipeHandlerType = typeof(THandler);
        ProfileTypes.Clear(); // Pipe mode excludes Profiles
    }
}