using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions;

public interface IChokaQNotifier
{
    Task NotifyJobUpdatedAsync(string jobId, JobStatus status, int attemptCount);
    Task NotifyJobProgressAsync(string jobId, int percentage);
}