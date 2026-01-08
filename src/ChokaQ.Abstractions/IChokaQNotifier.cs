using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions;

public interface IChokaQNotifier
{
    Task NotifyJobUpdatedAsync(string jobId, string type, JobStatus status, int attemptCount);
    Task NotifyJobProgressAsync(string jobId, int percentage);
}