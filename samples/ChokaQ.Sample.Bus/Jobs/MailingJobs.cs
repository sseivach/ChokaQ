using ChokaQ.Abstractions;

namespace ChokaQ.Sample.Bus.Jobs;

// --- DTOs ---
public record EmailJob(string To, string Subject) : ChokaQBaseJob;
public record SmsJob(string PhoneNumber, string Message) : ChokaQBaseJob;

// --- HANDLERS ---
public class EmailJobHandler : IChokaQJobHandler<EmailJob>
{
    private readonly ILogger<EmailJobHandler> _logger;
    public EmailJobHandler(ILogger<EmailJobHandler> logger) => _logger = logger;

    public async Task HandleAsync(EmailJob job, CancellationToken ct)
    {
        _logger.LogInformation("Sending Email to {To}: {Subject}", job.To, job.Subject);
        await Task.Delay(300, ct); // Simulate SMTP
    }
}

public class SmsJobHandler : IChokaQJobHandler<SmsJob>
{
    private readonly ILogger<SmsJobHandler> _logger;
    public SmsJobHandler(ILogger<SmsJobHandler> logger) => _logger = logger;

    public async Task HandleAsync(SmsJob job, CancellationToken ct)
    {
        _logger.LogInformation("Sending SMS to {Phone}: {Msg}", job.PhoneNumber, job.Message);
        await Task.Delay(100, ct); // Simulate Gateway
    }
}