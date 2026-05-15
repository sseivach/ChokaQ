using ChokaQ.Abstractions.Contexts;
using ChokaQ.Abstractions.Middleware;

namespace ChokaQ.Sample.NuGetLab.Middleware;

public sealed class LabAuditMiddleware : IChokaQMiddleware
{
    private readonly ILogger<LabAuditMiddleware> _logger;

    public LabAuditMiddleware(ILogger<LabAuditMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task InvokeAsync(IJobContext context, object? job, JobDelegate next)
    {
        var jobName = job?.GetType().Name ?? "unknown";
        _logger.LogInformation("Lab middleware entering {JobName} {JobId}", jobName, context.JobId);
        await context.ReportProgressAsync(2);

        try
        {
            await next();
            await context.ReportProgressAsync(100);
            _logger.LogInformation("Lab middleware completed {JobName} {JobId}", jobName, context.JobId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lab middleware observed failure in {JobName} {JobId}", jobName, context.JobId);
            throw;
        }
    }
}
