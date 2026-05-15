using ChokaQ.Abstractions.Exceptions;
using ChokaQ.Abstractions.Jobs;

namespace ChokaQ.Sample.NuGetLab.Jobs.Handlers;

public sealed class PoisonPillHandler : IChokaQJobHandler<PoisonPillJob>
{
    public Task HandleAsync(PoisonPillJob job, CancellationToken ct)
    {
        throw new ChokaQFatalException($"Fatal payload error in {job.Field}: {job.Reason}");
    }
}
