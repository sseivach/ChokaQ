using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions;

public interface IChokaQNotifier
{
    Task NotifyJobUpdatedAsync(string jobId, JobStatus status);
}