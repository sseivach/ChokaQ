using ChokaQ.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace ChokaQ.Core;

/// <summary>
/// Configuration options for the ChokaQ engine.
/// Allows selecting between "Bus" (Type-Safe) and "Pipe" (Raw) modes.
/// </summary>
public class ChokaQOptions
{
    internal bool IsPipeMode { get; private set; } = false;
    internal Type? PipeHandlerType { get; private set; }

    // Default to Bus mode, but assemblies might need to be explicit in future
    internal List<Assembly> ScanAssemblies { get; private set; } = new();

    /// <summary>
    /// Enables "Bus" mode (Default).
    /// Scans the provided assemblies for IChokaQJob implementations and Handlers.
    /// </summary>
    /// <param name="markerTypes">Types from assemblies to scan.</param>
    public void UseBus(params Type[] markerTypes)
    {
        IsPipeMode = false;
        foreach (var t in markerTypes)
        {
            ScanAssemblies.Add(t.Assembly);
        }
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
    }
}