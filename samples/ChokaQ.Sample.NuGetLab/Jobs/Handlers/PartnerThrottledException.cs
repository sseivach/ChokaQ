using ChokaQ.Abstractions.Exceptions;

namespace ChokaQ.Sample.NuGetLab.Jobs.Handlers;

public sealed class PartnerThrottledException : Exception, IChokaQThrottledException
{
    public PartnerThrottledException(TimeSpan retryAfter, string partner, int attempt)
        : base($"Partner {partner} throttled attempt {attempt}; retry after {retryAfter.TotalSeconds:0}s.")
    {
        RetryAfter = retryAfter;
    }

    public TimeSpan? RetryAfter { get; }
}
