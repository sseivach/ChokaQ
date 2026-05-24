# Smart Worker (Fast-Fail)

## Проблема retry storm

Retry policy, которая одинаково относится ко всем exceptions, может создать
такой pattern:

```text
Job fails with NullReferenceException
  → Retry #1 (wait 3s)... fails with NullReferenceException
  → Retry #2 (wait 6s)... fails with NullReferenceException
  → Retry #3 (wait 12s)... fails with NullReferenceException
  → Move to DLQ after 21 seconds of unsuccessful retries
```

Такой job вряд ли восстановится просто с течением времени:
`NullReferenceException` обычно означает bug в code или payload, а не временную
network condition. Retry добавляет delay, database writes и retry traffic, но
не повышает шанс success.

Smart Worker в ChokaQ разделяет failures, которые стоит retry, и failures,
которые нужно сразу отправить в DLQ для inspection.

## Fatal vs transient classification

Когда job throws exception, `JobProcessor` задает один вопрос:
**"Это code bug или временная проблема?"**

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

### Два пути

| Exception Type | Classification | Action | Retry Cost |
|---------------|---------------|--------|-------------|
| `NullReferenceException` | **Fatal** | -> DLQ immediately | ~0s |
| `ArgumentException` | **Fatal** | -> DLQ immediately | ~0s |
| `JsonException` | **Fatal** | -> DLQ immediately | ~0s |
| `HttpRequestException` | **Transient** | -> Exponential backoff retry | 3s -> 6s -> 12s |
| `TimeoutException` | **Transient** | -> Exponential backoff retry | 3s -> 6s -> 12s |
| `SqlException` transient | **Transient** | -> Exponential backoff retry | 3s -> 6s -> 12s |

### Decision tree

![Smart worker classification](/diagrams/39-smart-worker-classification.png)

## Exponential backoff with jitter

Когда transient error запускает retry, delay считается как **exponential growth
+ random jitter**:

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

**С default runtime configuration:**

| Attempt | Base Delay | + Jitter | Total |
|---------|-----------|----------|-------|
| 1 | 3,000ms | 0-1,000ms | ~3-4s |
| 2 | 6,000ms | 0-1,000ms | ~6-7s |
| 3 | 12,000ms | 0-1,000ms | ~12-13s |
| 4 | 24,000ms | 0-1,000ms | ~24-25s |
| 5 | 48,000ms | 0-1,000ms | ~48-49s |

::: warning Why Jitter?
Без jitter, если external API падает и 100 jobs fail одновременно, все 100
могут повториться в одно и то же время: 3s, 6s, 12s. Это Thundering Herd
problem. Jitter распределяет retries по короткому window, чтобы recovering
dependency получила более ровный traffic.
:::

## Custom fatal exceptions

Если у application есть domain-specific errors, которые никогда не нужно retry,
throw `ChokaQFatalException`:

```csharp
public class PaymentHandler : IChokaQJobHandler<ProcessPaymentJob>
{
    public async Task HandleAsync(ProcessPaymentJob job, CancellationToken ct)
    {
        var result = await _paymentGateway.ChargeAsync(job.Amount, ct);

        if (result.Status == PaymentStatus.CardDeclined)
        {
            // Retrying will not change this business outcome.
            throw new ChokaQFatalException(
                $"Card declined for order {job.OrderId}");
        }

        // Other errors (timeout, 500) can bubble up as transient.
    }
}
```

## Circuit Breaker integration

Smart Worker работает **вместе** с Circuit Breaker. Перед execution любого job
processor проверяет:

```csharp
// From: JobProcessor.ProcessAsync()

if (!_breaker.IsExecutionPermitted(job.Type))
{
    // Circuit is Open — delay execution instead of calling the dependency.
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

Если circuit permission granted, но user code не стартует, ChokaQ releases
execution permit вместо reporting success/failure. Это важно в HalfOpen state:
stale storage leases, admin cancellation before dispatch или shutdown не должны
оставить все probe slots consumed forever.

**Синергия:**

- Smart Worker обрабатывает **individual job failures**: fatal vs transient.
- Circuit Breaker обрабатывает **systemic failures**: если job type падает как
  группа, execution откладывается.

::: tip Architecture Insight
Smart Worker и Circuit Breaker работают вместе. Smart Worker reactive:
классифицирует individual jobs после failure. Circuit Breaker preventive:
блокирует весь job type до execution. Вместе они дают fault tolerance на micro
и macro уровнях.
:::

## Архитектурное решение

ChokaQ классифицирует failures до расходования retry budget, потому что не
каждая exception заслуживает еще одну execution attempt. Retry
`NullReferenceException`, invalid payload shape или unsupported domain state
только увеличивает queue lag и прячет реальный bug за шумом. Fast-fail fatal
errors в DLQ делает проблему видимой и сохраняет worker capacity для jobs,
которые действительно могут recover.

Альтернатива - uniform retry policy для каждой exception. Ее проще объяснить,
но она может увеличить retry traffic, database writes и queue lag, когда failure
не зависит от времени. Выбранный design принимает responsibility за
classification в обмен на более ясную incident visibility и более точное retry
behavior.

Classification должна оставаться conservative. Если failure может быть
transient, она должна оставаться retryable, пока application явно не пометит ее
fatal через `ChokaQFatalException`.

## Дополнительные вопросы

**Почему не retry каждый failed job до max attempts?**  
Потому что retry - инструмент recovery, а не correctness. Fatal code и payload
errors не улучшаются со временем, поэтому retries тратят capacity и задерживают
реальное transient recovery.

**Как избежать неправильной classification domain-specific failures?**  
Built-in list покрывает common programming и serialization failures. Domain
handlers могут throw `ChokaQFatalException`, когда business case точно
non-retryable.

**Как это взаимодействует с circuit breakers?**  
Smart Worker принимает per-job decision после failure. Circuit breakers
принимают job-type или queue-level decision до execution, когда failures
выглядят systemic.

<br>

> Дальше: полный путь job описан в [State Machine](/ru/2-lifecycle/state-machine).
