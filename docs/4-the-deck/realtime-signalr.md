# Real-time SignalR Dashboard

## The Deck: Mission Control

The Deck is ChokaQ's built-in admin dashboard — a **Blazor Server** application that provides real-time monitoring and operational control. No separate deployment, no external tools — it runs inside your ASP.NET Core process.

## Setup

```csharp
// Program.cs
builder.Services.AddAuthentication(/* your scheme */);
builder.Services.AddAuthorization();
builder.Services.AddChokaQTheDeck();

var app = builder.Build();
app.MapChokaQTheDeck();  // Dashboard available at /chokaq
```

By default, The Deck requires the host application's default authorization policy.
For a local-only demo or an intentionally public sandbox, opt in explicitly:

```csharp
builder.Services.AddChokaQTheDeck(options =>
{
    options.AllowAnonymousDeck = true;
});
```

## Architecture: Blazor Server + SignalR

<img src="/signalr_architecture.png" alt="SignalR Architecture Diagram" style="width: 100%; max-width: 900px; margin: 1.5rem auto; display: block;" />

**Why Blazor Server?**
- UI logic runs on the server — no API layer needed
- Direct access to `IJobStorage` via DI
- SignalR connection is already established by Blazor — zero extra config
- Real-time updates are **push-based**, not polling

## Dashboard Panels

### Stats Bar
Aggregated counters from `StatsSummary` + live `JobsHot` counts:

| Metric | Source | Update |
|--------|--------|--------|
| Pending | `COUNT(*) FROM JobsHot WHERE Status=0` | On every job state change |
| Buffered | `COUNT(*) FROM JobsHot WHERE Status=1` | On fetch |
| Processing | `COUNT(*) FROM JobsHot WHERE Status=2` | On process start/end |
| Succeeded | `StatsSummary.SucceededTotal` | On archive (O(1) read) |
| Failed | `StatsSummary.FailedTotal` | On DLQ move (O(1) read) |
| Throughput | `MetricBuckets` via `GetSystemHealthAsync()` | Dashboard refresh |
| Failure Rate | `MetricBuckets` via `GetSystemHealthAsync()` | Dashboard refresh |

### Operational Health Snapshot

The Deck reads `IJobStorage.GetSystemHealthAsync()` for production-oriented
signals that lifetime counters cannot answer:

- **Queue lag**: average and maximum wait time for eligible Pending jobs per queue.
- **Throughput**: jobs processed per second over the last 1 minute and 5 minutes.
- **Failure rate**: failed vs processed percentage over the same windows.
- **Top errors**: the top 5 recent DLQ error families grouped by `FailureReason`
  and normalized error prefix.

The SQL Server implementation keeps these reads bounded and index-friendly.
Queue lag is calculated from Pending hot rows through a dedicated filtered index,
throughput/failure rate come from rolling `MetricBuckets`, and top-error grouping
is limited to a recent DLQ sample so the dashboard does not become an accidental
full-history analytics workload during an incident.

See [Rolling Observability Buckets](/4-the-deck/rolling-observability) for the
architecture lesson behind the upgrade from direct Archive/DLQ lookbacks to
materialized outcome buckets.

### Consistency Model

The Deck treats `IJobStorage` as the canonical source of truth after every
operator mutation. It does not decrement counters locally after retry, purge,
cancel, or edit commands, because those commands can be affected by filters,
ownership guards, concurrent workers, and row-level outcomes. Instead, the page
waits for the command path to settle and then reloads summary counters, health,
circuit state, and the current table slice from storage.

This deliberately trades a tiny refresh delay for correctness. The operator sees
the state the database accepted, not the state the browser guessed. It also keeps
bulk operations understandable: if a filtered purge removes the last item on a
history page, The Deck clamps the view to the new last page instead of showing an
empty page while the global counters still report matching rows.

Lag colors are configurable:

```csharp
builder.Services.AddChokaQTheDeck(options =>
{
    options.QueueLagWarningThresholdSeconds = 5;
    options.QueueLagCriticalThresholdSeconds = 10;
});
```

### Job Matrix
Virtualized grid showing active jobs with color-coded status badges:

| Badge | Color | Meaning |
|-------|-------|---------|
| `PENDING` | Gray/Muted | Waiting in queue |
| `FETCHED` | Blue | In worker memory buffer |
| `PROCESSING` | Yellow/Warning | Actively executing |
| `SUCCEEDED` | Green | Completed (in Archive view) |
| `FAILED` | Red | Failed (in DLQ view) |

DLQ rows also show a failure-taxonomy badge. The badge is stored as structured
data, not inferred from exception text:

| Reason | Typical Meaning |
|--------|-----------------|
| `FatalError` | Poison pill or non-retryable handler/payload error |
| `Throttled` | Downstream rate limit or quota pressure |
| `Timeout` | Execution exceeded runtime budget |
| `Transient` | Retryable failure exhausted retry budget |
| `Cancelled` | Explicit operator cancellation |
| `Zombie` | Worker heartbeat expired |

This split matters operationally: throttling points to concurrency/quotas,
timeouts point to execution budgets or downstream latency, and fatal errors point
to payload or code fixes.

### Queue Management Panel
Runtime controls for every queue:

- **Pause/Resume** — stop fetching from a queue without affecting in-flight jobs
- **Activate/Deactivate** — completely disable a queue
- **MaxWorkers** — adjust Bulkhead concurrency limit
- **Zombie Timeout** — per-queue heartbeat threshold

### Circuit Breaker Monitor
Visual indicators for each job type's circuit status:

| Indicator | State | Action |
|-----------|-------|--------|
| 🟢 | Closed | All clear — executions permitted |
| 🟡 | Half-Open | Testing — one execution allowed |
| 🔴 | Open | Blocked — countdown to reset shown |

### Console Stream
Real-time log of system events:

```
14:32:05  ✅ Job job-abc123 completed (245ms) [email_v1]
14:32:07  ❌ Job job-def456 failed: NullReferenceException [report_v2]
14:32:07  ⚡ Smart Worker: Fatal error — sent to DLQ immediately
14:33:00  ⚠️ ZombieRescue: Recovered 1 abandoned, archived 0 zombies
14:33:15  🔴 Circuit OPEN for job type "payment_v1" (5 consecutive failures)
```

## Two Visual Themes

### Blueprint (Default)
Engineering blueprint aesthetic — dark navy background with white grid lines:
- Background: `#003366` with CSS grid overlay
- Font: `Consolas` monospace
- Borders: white with opacity
- Glass effect with `backdrop-filter: blur(13px)`

### Carbon
High-contrast dark mode inspired by IBM Carbon Design System:
- Background: `#222` with carbon fiber pattern
- Font: `Inter` sans-serif
- Borders: solid gray
- No glass effects — solid panels

Switch between themes at runtime via the theme selector in the header.

## SignalR Notification Flow

When a job changes state, the pipeline is:

```
JobProcessor completes job
    → IJobStorage.ArchiveSucceededAsync()    // Persist
    → IChokaQMetrics.RecordSuccess()         // Metrics
    → IChokaQNotifier.NotifyCompletedAsync() // Push to dashboard
        → SignalR Hub broadcasts to all connected clients
            → Blazor components re-render with new data
```

The `IChokaQNotifier` is the bridge between the processing engine and the UI. In SQL Server mode, it uses SignalR. In In-Memory mode, it's a `NullNotifier` (no-op).

## Security

The Deck is an administrative control plane: it can cancel, purge, edit, pause,
deactivate, and resurrect jobs. For production, bind it to a dedicated ASP.NET
Core authorization policy:

```csharp
builder.Services.AddChokaQTheDeck(options =>
{
    options.AuthorizationPolicy = "ChokaQAdmin";
    options.DestructiveAuthorizationPolicy = "ChokaQWrite";
});

// Define the policy
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ChokaQAdmin", policy =>
        policy.RequireRole("Admin"));
    options.AddPolicy("ChokaQWrite", policy =>
        policy.RequireRole("JobOperator"));
});
```

If no named policy is configured, The Deck still calls `RequireAuthorization()`
and uses the application's default policy. Public access requires
`AllowAnonymousDeck = true`, which keeps demos convenient without making
production exposure the default.

`AuthorizationPolicy` controls read/connect access to the dashboard and SignalR
Hub. `DestructiveAuthorizationPolicy` is checked inside Hub methods before
cancel, retry, resurrect, edit, purge, pause, deactivate, or queue-limit changes.
Leaving it null means destructive commands use the same boundary as the dashboard.

> *Next: See how to [Edit + Resurrect](/4-the-deck/resurrect-dlq) dead jobs directly from the browser.*
