# Real-time SignalR Dashboard

## The Deck: Mission Control

The Deck is ChokaQ's built-in admin dashboard — a **Blazor Server** application that provides real-time monitoring and operational control. No separate deployment, no external tools — it runs inside your ASP.NET Core process.

## Setup

```csharp
// Program.cs
builder.Services.AddChokaQTheDeck();

var app = builder.Build();
app.MapChokaQTheDeck();  // Dashboard available at /chokaq
```

That's it. Two lines.

## Architecture: Blazor Server + SignalR

```
┌──────────────┐     WebSocket      ┌──────────────┐
│   Browser    │◄──────────────────▶│  ASP.NET Core │
│  (The Deck)  │   (SignalR Hub)    │   Process     │
│              │                    │               │
│  Blazor WASM │                    │  ChokaQ Hub   │
│  Components  │                    │  ├─ OnJobCompleted()
│              │                    │  ├─ OnJobFailed()
│              │                    │  ├─ OnStatsUpdated()
│              │                    │  └─ OnConsoleMessage()
└──────────────┘                    └──────────────┘
```

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

### Job Matrix
Virtualized grid showing active jobs with color-coded status badges:

| Badge | Color | Meaning |
|-------|-------|---------|
| `PENDING` | Gray/Muted | Waiting in queue |
| `FETCHED` | Blue | In worker memory buffer |
| `PROCESSING` | Yellow/Warning | Actively executing |
| `SUCCEEDED` | Green | Completed (in Archive view) |
| `FAILED` | Red | Failed (in DLQ view) |

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

The Deck supports ASP.NET Core Authorization Policies:

```csharp
builder.Services.AddChokaQTheDeck(options =>
{
    options.AuthorizationPolicy = "ChokaQAdmin";
});

// Define the policy
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ChokaQAdmin", policy =>
        policy.RequireRole("Admin"));
});
```

If no policy is configured, the dashboard is publicly accessible — convenient for development.

> *Next: See how to [Edit + Resurrect](/4-the-deck/resurrect-dlq) dead jobs directly from the browser.*
