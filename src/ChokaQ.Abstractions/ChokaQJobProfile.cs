using System;
using System.Collections.Generic;

namespace ChokaQ.Abstractions;

/// <summary>
/// Base class for defining job configurations.
/// Allows explicit mapping between Job Keys, DTOs, and Handlers.
/// </summary>
public abstract class ChokaQJobProfile
{
    /// <summary>
    /// The list of configured job mappings. 
    /// Must be public so ChokaQ.Core can read it during startup.
    /// </summary>
    public List<JobRegistration> Registrations { get; } = new();

    /// <summary>
    /// Registers a job type and its corresponding handler with a specific key.
    /// </summary>
    /// <typeparam name="TJob">The job DTO type (must implement IChokaQJob).</typeparam>
    /// <typeparam name="THandler">The handler type (must implement IChokaQJobHandler<TJob>).</typeparam>
    /// <param name="typeKey">The unique string key identifying this job type (stored in DB).</param>
    protected void CreateJob<TJob, THandler>(string typeKey)
        where TJob : IChokaQJob
        where THandler : class, IChokaQJobHandler<TJob>
    {
        if (string.IsNullOrWhiteSpace(typeKey))
            throw new ArgumentNullException(nameof(typeKey));

        Registrations.Add(new JobRegistration(typeKey, typeof(TJob), typeof(THandler)));
    }
}

/// <summary>
/// DTO to hold registration info.
/// Made public so ChokaQ.Core can access the types during DI registration.
/// </summary>
public record JobRegistration(string Key, Type JobType, Type HandlerType);