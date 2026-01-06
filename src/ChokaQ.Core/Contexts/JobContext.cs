using ChokaQ.Abstractions;

namespace ChokaQ.Core.Contexts;

internal class JobContext : IJobContext
{
    private readonly IChokaQNotifier _notifier;

    // Will be set by the Worker before the handler starts
    public string JobId { get; set; } = string.Empty;

    public JobContext(IChokaQNotifier notifier)
    {
        _notifier = notifier;
    }

    public async Task ReportProgressAsync(int percentage)
    {
        if (string.IsNullOrEmpty(JobId)) return;

        percentage = Math.Max(0, Math.Min(100, percentage));

        await _notifier.NotifyJobProgressAsync(JobId, percentage);
    }
}