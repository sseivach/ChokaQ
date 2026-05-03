using Microsoft.Extensions.Hosting;

namespace ChokaQ.Core.Workers;

/// <summary>
/// Hosts the default in-memory worker while keeping the worker manager as the same singleton instance.
/// </summary>
public sealed class JobWorkerHostedService : IHostedService
{
    private readonly JobWorker _worker;

    public JobWorkerHostedService(JobWorker worker)
    {
        _worker = worker;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The in-memory worker is both the runtime worker and the management surface
        // exposed through IWorkerManager. Hosting the same singleton avoids split-brain
        // control where the dashboard manages one worker instance while the host runs another.
        return _worker.StartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => _worker.StopAsync(cancellationToken);
}
