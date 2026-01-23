using ChokaQ.Abstractions;

namespace ChokaQ.SampleRun.Jobs;

// --- DTOs ---
public record HealthCheckJob(string TargetService) : ChokaQBaseJob;
public record CleanupJob(int RetentionDays) : ChokaQBaseJob;

// --- HANDLERS ---
public class HealthCheckHandler : IChokaQJobHandler<HealthCheckJob>
{
    private readonly ILogger<HealthCheckHandler> _logger;
    public HealthCheckHandler(ILogger<HealthCheckHandler> logger) => _logger = logger;

    public async Task HandleAsync(HealthCheckJob job, CancellationToken ct)
    {
        _logger.LogInformation("Checking health of {Service}...", job.TargetService);
        await Task.Delay(50, ct);
    }
}

public class CleanupJobHandler : IChokaQJobHandler<CleanupJob>
{
    private readonly ILogger<CleanupJobHandler> _logger;
    public CleanupJobHandler(ILogger<CleanupJobHandler> logger) => _logger = logger;

    public async Task HandleAsync(CleanupJob job, CancellationToken ct)
    {
        _logger.LogWarning("Cleaning up data older than {Days} days...", job.RetentionDays);
        await Task.Delay(1000, ct);
    }
}