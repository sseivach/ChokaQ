using ChokaQ.Abstractions.Workers;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace ChokaQ.Core.Health;

/// <summary>
/// Reports whether the local ChokaQ worker service is alive.
/// </summary>
/// <remarks>
/// This check is intentionally process-local. SQL can tell us about queued work, but it cannot
/// tell us whether this host's BackgroundService actually entered and continues cycling its
/// fetch/processor loop. That distinction matters for orchestrators: a pod with healthy SQL
/// connectivity but a dead worker should not be treated as ready.
/// </remarks>
internal sealed class ChokaQWorkerHealthCheck : IHealthCheck
{
    private readonly IWorkerManager _workerManager;
    private readonly ChokaQHealthCheckOptions _options;

    public ChokaQWorkerHealthCheck(
        IWorkerManager workerManager,
        IOptions<ChokaQHealthCheckOptions> options)
    {
        _workerManager = workerManager;
        _options = options.Value;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>
        {
            ["activeWorkers"] = _workerManager.ActiveWorkers,
            ["totalWorkers"] = _workerManager.TotalWorkers,
            ["isRunning"] = _workerManager.IsRunning
        };

        if (_workerManager.LastHeartbeatUtc is { } heartbeat)
        {
            data["lastHeartbeatUtc"] = heartbeat;
            data["heartbeatAgeSeconds"] = Math.Max(0, (DateTimeOffset.UtcNow - heartbeat).TotalSeconds);
        }

        if (!_workerManager.IsRunning)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "ChokaQ worker service has not entered its runtime loop.",
                data: data));
        }

        if (_workerManager.TotalWorkers <= 0)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "ChokaQ worker capacity is zero; no jobs can be processed.",
                data: data));
        }

        if (_workerManager.LastHeartbeatUtc is not { } lastHeartbeatUtc)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "ChokaQ worker has not emitted a heartbeat yet.",
                data: data));
        }

        var heartbeatAge = DateTimeOffset.UtcNow - lastHeartbeatUtc;
        if (heartbeatAge > _options.WorkerHeartbeatTimeout)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"ChokaQ worker heartbeat is stale. Age={heartbeatAge.TotalSeconds:n1}s, Timeout={_options.WorkerHeartbeatTimeout.TotalSeconds:n1}s.",
                data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy("ChokaQ worker is alive.", data));
    }
}
