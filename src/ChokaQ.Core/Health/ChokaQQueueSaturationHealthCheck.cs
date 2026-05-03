using ChokaQ.Abstractions.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace ChokaQ.Core.Health;

/// <summary>
/// Reports queue saturation from pending-job lag.
/// </summary>
/// <remarks>
/// Queue depth alone is not a reliable health signal. Ten slow jobs can be worse than ten
/// thousand tiny jobs. Lag measures whether pending work is waiting longer than the system's
/// operational budget, which makes it a better readiness/alerting signal.
/// </remarks>
internal sealed class ChokaQQueueSaturationHealthCheck : IHealthCheck
{
    private readonly IJobStorage _storage;
    private readonly ChokaQHealthCheckOptions _options;

    public ChokaQQueueSaturationHealthCheck(
        IJobStorage storage,
        IOptions<ChokaQHealthCheckOptions> options)
    {
        _storage = storage;
        _options = options.Value;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var health = await _storage.GetSystemHealthAsync(cancellationToken);
        var worstQueue = health.Queues
            .OrderByDescending(queue => queue.MaxLagSeconds)
            .FirstOrDefault();

        var data = new Dictionary<string, object>
        {
            ["generatedAtUtc"] = health.GeneratedAtUtc,
            ["queueCount"] = health.Queues.Count,
            ["jobsPerSecondLastMinute"] = health.JobsPerSecondLastMinute,
            ["failureRateLastMinutePercent"] = health.FailureRateLastMinutePercent
        };

        if (worstQueue is not null)
        {
            data["worstQueue"] = worstQueue.Queue;
            data["worstQueuePending"] = worstQueue.Pending;
            data["worstQueueMaxLagSeconds"] = worstQueue.MaxLagSeconds;
        }

        var unhealthyLagSeconds = _options.QueueLagUnhealthyThreshold.TotalSeconds;
        var degradedLagSeconds = _options.QueueLagDegradedThreshold.TotalSeconds;

        if (worstQueue is not null && worstQueue.MaxLagSeconds > unhealthyLagSeconds)
        {
            return HealthCheckResult.Unhealthy(
                $"Queue '{worstQueue.Queue}' lag is critical at {worstQueue.MaxLagSeconds:n1}s.",
                data: data);
        }

        if (worstQueue is not null && worstQueue.MaxLagSeconds > degradedLagSeconds)
        {
            return HealthCheckResult.Degraded(
                $"Queue '{worstQueue.Queue}' lag is elevated at {worstQueue.MaxLagSeconds:n1}s.",
                data: data);
        }

        return HealthCheckResult.Healthy("ChokaQ queue lag is within configured thresholds.", data);
    }
}
