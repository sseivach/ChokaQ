using System;

namespace ChokaQ.Abstractions.Exceptions;

/// <summary>
/// Marker interface for exceptions indicating that the external dependency is throttling requests.
/// 
/// [RESILIENCY PATTERN - "The Good Neighbor"]:
/// When an external API returns HTTP 429 (Too Many Requests), falling back to standard 
/// exponential backoff is dangerous. If the service tells you to wait 60 seconds (via Retry-After), 
/// you MUST wait exactly 60 seconds. Ignoring this causes Retry Storms and prolongs external outages.
/// 
/// Jobs failing with this exception will explicitly pause for <see cref="RetryAfter"/> before retrying.
/// </summary>
public interface IChokaQThrottledException
{
    /// <summary>
    /// The exact delay to wait before retrying the job.
    /// Maps to the Retry-After HTTP header.
    /// </summary>
    TimeSpan? RetryAfter { get; }
}
