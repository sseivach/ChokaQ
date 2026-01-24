using ChokaQ.Abstractions;

namespace ChokaQ.Sample.Bus.Jobs;

// --- DTOs ---
public record EmployeeReportJob(string DeptId) : ChokaQBaseJob;
public record SalesReportJob(int Year, int Month) : ChokaQBaseJob;
public record ExpenseReportJob(string CostCenter) : ChokaQBaseJob;

// --- HANDLERS ---
public class ReportingHandler<T> : IChokaQJobHandler<T> where T : IChokaQJob
{
    private readonly ILogger<ReportingHandler<T>> _logger;
    private readonly IJobContext _context;

    public ReportingHandler(ILogger<ReportingHandler<T>> logger, IJobContext context)
    {
        _logger = logger;
        _context = context;
    }

    public async Task HandleAsync(T job, CancellationToken ct)
    {
        var reportName = typeof(T).Name.Replace("Job", "");
        _logger.LogInformation("Generating {Report}...", reportName);

        // Simulate heavy work with progress
        for (int i = 0; i <= 100; i += 20)
        {
            await Task.Delay(200, ct);
            await _context.ReportProgressAsync(i);
        }

        _logger.LogInformation("{Report} saved to storage.", reportName);
    }
}