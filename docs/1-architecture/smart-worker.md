# Smart Worker (Fast-Fail)

## The Retry Storm Problem

A typical background job framework processes errors like this:

```
Job fails with NullReferenceException
  → Retry #1 (wait 3s)... fails with NullReferenceException
  → Retry #2 (wait 6s)... fails with NullReferenceException
  → Retry #3 (wait 12s)... fails with NullReferenceException
  → Move to DLQ after 21 seconds of wasted compute
```

This job had **zero chance of succeeding** on retry — `NullReferenceException` is a code bug, not a network blip. Yet the system burned 21 seconds of CPU, 3 database round-trips, and 3 retry slots that could've served transient failures.

**ChokaQ's Smart Worker fixes this.**

## Fatal vs Transient Classification

When a job throws an exception, the `JobProcessor` asks one question: **"Is this a code bug or a temporary problem?"**

```csharp
// From: ChokaQ.Core/Processing/JobProcessor.cs

private static bool IsFatalException(Exception ex)
{
    return ex is ArgumentException
        or ArgumentNullException
        or NullReferenceException
        or InvalidOperationException
        or InvalidCastException
        or FormatException
        or NotImplementedException
        or NotSupportedException
        or System.Text.Json.JsonException
        or ChokaQFatalException;         // User-defined fatal marker
}
```

### The Two Paths

| Exception Type | Classification | Action | Time Wasted |
|---------------|---------------|--------|-------------|
| `NullReferenceException` | **Fatal** | → DLQ immediately | ~0s |
| `ArgumentException` | **Fatal** | → DLQ immediately | ~0s |
| `JsonException` | **Fatal** | → DLQ immediately | ~0s |
| `HttpRequestException` | **Transient** | → Exponential backoff retry | 3s → 6s → 12s |
| `TimeoutException` | **Transient** | → Exponential backoff retry | 3s → 6s → 12s |
| `SqlException` (transient) | **Transient** | → Exponential backoff retry | 3s → 6s → 12s |

### The Decision Tree

![Smart worker classification](/diagrams/39-smart-worker-classification.png)

## Exponential Backoff with Jitter

When a transient error triggers a retry, the delay is calculated with **exponential growth + random jitter**:

```csharp
// From: ChokaQ.Core/Processing/JobProcessor.cs

private int CalculateBackoffMs(int attempt)
{
    // BaseDelay: 3s, 6s, 12s, 24s, 48s, ...
    var calculatedDelayMs =
        options.Retry.BaseDelay.TotalMilliseconds *
        Math.Pow(options.Retry.BackoffMultiplier, attempt - 1);

    // MaxDelay is a true cap. Jitter is clipped instead of added past it.
    var cappedDelayMs = Math.Min(
        calculatedDelayMs,
        options.Retry.MaxDelay.TotalMilliseconds);

    // Jitter: random 0-1000ms by default to prevent thundering herd.
    var jitterWindowMs = Math.Min(
        options.Retry.JitterMaxDelay.TotalMilliseconds,
        Math.Max(0, options.Retry.MaxDelay.TotalMilliseconds - cappedDelayMs));

    return (int)(cappedDelayMs + Random.Shared.NextDouble() * jitterWindowMs);
}
```

**With default runtime configuration:**

| Attempt | Base Delay | + Jitter | Total |
|---------|-----------|----------|-------|
| 1 | 3,000ms | 0–1,000ms | ~3–4s |
| 2 | 6,000ms | 0–1,000ms | ~6–7s |
| 3 | 12,000ms | 0–1,000ms | ~12–13s |
| 4 | 24,000ms | 0–1,000ms | ~24–25s |
| 5 | 48,000ms | 0–1,000ms | ~48–49s |

::: warning 🎯 Why Jitter?
Without jitter, if an external API goes down and 100 jobs fail simultaneously, all 100 will retry at **exactly** the same time (3s, 6s, 12s...). This is the **Thundering Herd** problem — the retry storm itself becomes a DDoS attack on the recovering service. Jitter spreads retries across a 1-second window, smoothing the load.
:::

## Custom Fatal Exceptions

If your application has domain-specific errors that should never be retried, throw `ChokaQFatalException`:

```csharp
public class PaymentHandler : IChokaQJobHandler<ProcessPaymentJob>
{
    public async Task HandleAsync(ProcessPaymentJob job, CancellationToken ct)
    {
        var result = await _paymentGateway.ChargeAsync(job.Amount, ct);

        if (result.Status == PaymentStatus.CardDeclined)
        {
            // No point retrying — the card is declined
            throw new ChokaQFatalException(
                $"Card declined for order {job.OrderId}");
        }

        // Other errors (timeout, 500) will bubble up as transient
    }
}
```

## Circuit Breaker Integration

The Smart Worker works **alongside** the Circuit Breaker. Before executing any job, the processor checks:

```csharp
// From: JobProcessor.ProcessAsync()

if (!_breaker.IsExecutionPermitted(job.Type))
{
    // Circuit is Open — don't even try
    await _storage.ArchiveFailedAsync(job.Id,
        "Circuit breaker is open for this job type");
    return;
}

try
{
    await _dispatcher.DispatchAsync(job, ct);
    _breaker.ReportSuccess(job.Type);    // Reset failure counter
}
catch (Exception ex)
{
    _breaker.ReportFailure(job.Type);    // Increment failure counter
    // ... Smart Worker classification happens here
}
```

If circuit permission is granted but user code never starts, ChokaQ releases
the execution permit instead of reporting success or failure. This matters in
HalfOpen state: stale storage leases, admin cancellation before dispatch, or
shutdown should not leave all probe slots consumed forever.

**The synergy:**
- Smart Worker handles **individual job failures** (fatal vs transient)
- Circuit Breaker handles **systemic failures** (if ALL jobs of type X are failing, stop trying)

::: tip 💡 Architecture Insight
The Smart Worker and Circuit Breaker act together. Smart Worker is **reactive** (classifies individual jobs after failure). Circuit Breaker is **preventive** (blocks the entire queue before execution). Together they provide both micro-level and macro-level fault tolerance.
:::

## Architecture Decision

ChokaQ classifies failures before spending retry budget because not every
exception deserves another execution attempt. Retrying `NullReferenceException`,
invalid payload shape, or unsupported domain state only increases queue lag and
hides the real bug behind noise. Fast-failing fatal errors into DLQ makes the
problem visible and preserves worker capacity for jobs that can actually
recover.

The alternative is a uniform retry policy for every exception. That is simpler
to explain, but operationally weaker: a bad deployment can create a retry storm,
inflate database writes, and delay healthy queues. The chosen design accepts a
classification responsibility in exchange for faster incident visibility and
less wasted compute.

Classification must stay conservative. If a failure might be transient, it
should remain retryable unless the application explicitly marks it fatal with
`ChokaQFatalException`.

## Interview Questions

**Why not let every failed job retry until max attempts?**  
Because retry is a recovery tool, not a correctness tool. Fatal code and payload
errors do not improve with time, so retrying them burns capacity and delays real
transient recovery.

**How do you avoid misclassifying domain-specific failures?**  
The built-in list covers common programming and serialization failures. Domain
handlers can throw `ChokaQFatalException` when the business case is known to be
non-retryable.

**How does this interact with circuit breakers?**  
Smart Worker makes a per-job decision after a failure. Circuit breakers make a
job-type or queue-level decision before execution when failures look systemic.

<br>

> *Next: Follow a job through its complete lifecycle in [The State Machine](/2-lifecycle/state-machine).*
