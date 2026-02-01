using ChokaQ.Abstractions.Jobs;

namespace ChokaQ.Sample.Bus.Jobs;

public record EmailJob(string To, string Subject) : ChokaQBaseJob;
public record SmsJob(string PhoneNumber, string Message) : ChokaQBaseJob;

public class EmailJobHandler : IChokaQJobHandler<EmailJob>
{
    private readonly ILogger<EmailJobHandler> _logger;

    public EmailJobHandler(ILogger<EmailJobHandler> logger) => _logger = logger;

    public async Task HandleAsync(EmailJob job, CancellationToken ct)
    {
        // Simulate heavy processing / network latency
        await Task.Delay(500, ct);

        // Generate a random number between 0 and 9
        var roll = Random.Shared.Next(0, 10);

        // In 70% of cases (0..6), simulate an SMTP server failure
        if (roll < 7)
        {
            var errors = new[] { "SMTP Timeout", "Connection Refused", "Greylisting active", "Auth failed" };
            var error = errors[Random.Shared.Next(errors.Length)];

            _logger.LogError("🧨 Oops! Failed to send email to {To}. Reason: {Error}", job.To, error);

            // THROW EXCEPTION -> THIS TRIGGERS SMART RETRY
            throw new Exception($"Simulated SMTP Error: {error}");
        }

        // In 30% of cases, success
        _logger.LogInformation("✅ Email successfully sent to {To}: {Subject}", job.To, job.Subject);
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