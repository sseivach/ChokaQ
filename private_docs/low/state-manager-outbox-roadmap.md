# State Manager And Notification Outbox Roadmap

This file tracks hardening work for `JobStateManager`, state-transition
notifications, and the future outbox model. It is intentionally private and not
linked from the public documentation navigation.

Priority: low.

Reason: the current design does not corrupt job state when notifications fail,
but it can leave the dashboard stale, create notification storms, and keep UI
delivery retry work inside the state-transition path. This is operational
hardening for a later release, not a first NuGet preview blocker.

## Current State

Strong parts already implemented:

- `IJobStorage` owns persistence.
- `JobStateManager` owns state-transition orchestration.
- `IChokaQNotifier` owns dashboard notification delivery.
- Storage entities are not exposed directly to the UI notification contract.
- State transitions suppress notifications when storage reports that no row was
  moved.
- Notification failures are caught and logged so SignalR failures do not crash
  the job processor.
- Timeout and cancellation taxonomy is already separated before notification.

Important correction:

- `SafeNotifyAsync` is not truly fire-and-forget. It is awaited by
  `JobStateManager`, so notification retries can add latency to the
  state-transition path even though exceptions are swallowed.

Remaining risks:

- Storage transitions and UI notifications are not committed atomically.
- If a notification fails after the storage transition succeeds, the dashboard
  can remain stale until another refresh path corrects it.
- There is no durable notification replay mechanism.
- `NotifyStatsUpdatedAsync` is called after many individual transitions and can
  produce excessive SignalR traffic under load.
- Notification retry now uses a short exponential delay with jitter.
- `SafeNotifyAsync` observes the transition cancellation token before retrying
  and while waiting between retries.
- `JobStateManager` uses `DateTime.UtcNow` for notification DTOs, while storage
  may use SQL server time for persisted timestamps.
- Notification delivery is not idempotent or deduplicated.
- There is no event model that separates domain state changes from SignalR
  transport calls.

## Design Principles

- Storage state is authoritative.
- UI notifications are derived events, not the source of truth.
- A failed UI delivery must never roll back or corrupt job execution.
- A successful storage transition should create a durable event when reliable UI
  delivery is required.
- Notification bursts should be coalesced where exact per-transition delivery is
  not needed.
- State-transition code should not own transport retry policy long term.
- Timestamps should come from one explicit clock boundary.

## Status Legend

| Status | Meaning |
|---|---|
| Done | Implemented, tested, and documented for current scope. |
| Partial | Some behavior exists, but the acceptance criteria below are not complete. |
| Open | Not implemented yet. |
| Deferred | Intentionally postponed until higher-risk items are complete. |

## Executive Sequence

1. Keep the current no-false-notify guard when storage moves zero rows.
2. Add cancellable notification retry with exponential backoff and jitter.
3. Coalesce stats notifications to reduce SignalR noise.
4. Introduce a typed job-state event model.
5. Add an outbox table or storage abstraction for durable state-change events.
6. Move SignalR delivery to a background outbox dispatcher.
7. Add event idempotency, deduplication, and replay tests.

## Latest Audit Classification

| Finding | Current Status | Action |
|---|---|---|
| Storage and notification have no transactional boundary. | Valid. Storage is authoritative, but UI delivery is not durable or replayable. | Add outbox in a later hardening step. |
| Archive failure can still notify success. | Mostly mitigated. Current code awaits storage first and skips notification when no row moves. Partial storage failures still need storage-level guarantees. | Keep zero-row guards and cover storage exception behavior in tests. |
| Notify failure can leave UI stale. | Valid. Exceptions are swallowed after retries. | Add durable outbox or periodic reconciliation. |
| `NotifyStatsUpdatedAsync` can spam clients. | Valid. It is called after many individual transitions. | Add debounce or batching. |
| `SafeNotifyAsync` retry is too simple. | Mitigated for preview. It now uses bounded exponential delay with jitter. | Keep durable outbox as the later hardening step. |
| Cancellation token is not propagated. | Mitigated for preview. `SafeNotifyAsync` observes the transition token before and during retry delay. | Keep direct notifier APIs unchanged until outbox work. |
| StateManager time can diverge from storage time. | Valid. DTO timestamp uses local process UTC time. | Use `ISystemClock` or storage-returned transition timestamps. |
| StateManager has no idempotent event model. | Valid. Notifications can duplicate and are transport-shaped. | Add typed events with event IDs. |

## Phase A: Preserve Transition Correctness

Goal: keep the current storage-first behavior that prevents false dashboard
state.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| A.1 | P0 | Done | Call storage before notification. | Notifications are emitted only after storage returns success. |
| A.2 | P0 | Done | Suppress notification on zero-row transition. | Lost ownership or stale finalization does not emit a false UI event. |
| A.3 | P1 | Done | Add storage-exception notification tests. | If storage throws, no notification is sent. |
| A.4 | P1 | Done | Add transition result contract docs. | Storage implementations document what `false`, success, and exception mean. |

## Phase B: Notification Retry Hardening

Goal: make current direct notification safer while outbox is not implemented.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| B.1 | P1 | Done | Swallow notification exceptions. | Current code logs final failure and does not crash execution. |
| B.2 | P1 | Done | Pass cancellation token into notification retry. | Retry delay and notifier calls can stop during shutdown. |
| B.3 | P1 | Done | Add exponential backoff. | Retry delay grows per attempt within a bounded maximum. |
| B.4 | P1 | Done | Add jitter. | Concurrent failures do not retry in lockstep. |
| B.5 | P2 | Open | Add notification retry metrics. | Operators can see attempts, failures, final drops, and retry latency. |

Candidate behavior:

```csharp
var delay = Backoff.WithJitter(baseDelay, attempt, maxDelay);
await Task.Delay(delay, ct);
```

## Phase C: Stats Notification Coalescing

Goal: avoid turning high job throughput into high SignalR traffic.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| C.1 | P1 | Partial | Emit stats updates after state changes. | Current code notifies stats after archive, DLQ, cancel, and retry transitions. |
| C.2 | P1 | Open | Add stats debounce. | Multiple state changes within a short window produce one stats refresh event. |
| C.3 | P1 | Open | Add batch stats notification path. | Bulk operations can emit one summary event instead of one event per row. |
| C.4 | P2 | Open | Add dashboard reconciliation interval. | The Deck periodically refreshes stats even if a notification is dropped. |

Recommended direction:

- Keep per-job events for visible job rows.
- Debounce global stats updates because exact per-transition stats pushes are
  usually unnecessary.

## Phase D: Job State Event Model

Goal: separate domain events from SignalR transport methods.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| D.1 | P1 | Open | Add `JobStateChangedEvent`. | A single event model represents processing, retry, archived, failed, cancelled, and resurrected changes. |
| D.2 | P1 | Open | Add stable event IDs. | Each event has a unique ID for deduplication and replay. |
| D.3 | P1 | Open | Add event version field. | Event schema can evolve without breaking old consumers. |
| D.4 | P2 | Open | Map events to notifier calls. | SignalR delivery becomes a projection of the event model. |

Example shape:

```csharp
public sealed record JobStateChangedEvent(
    Guid EventId,
    string JobId,
    string Queue,
    JobStatus Status,
    string? Reason,
    DateTime OccurredAtUtc,
    int Version = 1);
```

## Phase E: Durable Outbox

Goal: make state-change notification delivery replayable.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| E.1 | P1 | Open | Add outbox storage abstraction. | Storage can append a state-change event in the same durability boundary as a transition. |
| E.2 | P1 | Open | Add SQL outbox table. | SQL Server storage persists pending notification events with status, attempts, and timestamps. |
| E.3 | P1 | Open | Write transition and event atomically. | Archive, DLQ, cancel, retry, and processing transitions can include an outbox event. |
| E.4 | P1 | Open | Add outbox cleanup policy. | Delivered events are retained long enough for diagnostics and then purged safely. |
| E.5 | P2 | Open | Add outbox admin view. | Operators can inspect stuck notification events if needed. |

Important note:

The outbox is not required for job execution correctness. It is required if The
Deck notification delivery is expected to be durable and replayable.

## Phase F: Background Notification Dispatcher

Goal: remove transport retry from the state-transition path.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| F.1 | P1 | Open | Add outbox dispatcher service. | Background service reads pending outbox events and delivers SignalR notifications. |
| F.2 | P1 | Open | Add delivery lease. | Multiple process instances do not deliver the same event concurrently. |
| F.3 | P1 | Open | Add delivery retry policy. | Failed delivery retries with backoff, jitter, and max attempts or quarantine. |
| F.4 | P1 | Open | Add idempotent delivery handling. | Duplicate delivery does not corrupt dashboard state. |
| F.5 | P2 | Open | Add dispatcher metrics. | Pending count, oldest age, delivery rate, failure rate, and dead events are observable. |

## Phase G: Clock Boundary

Goal: make notification timestamps consistent with persisted state.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| G.1 | P1 | Partial | Use UTC timestamps. | Current code uses `DateTime.UtcNow` in `JobStateManager`. |
| G.2 | P1 | Open | Introduce `ISystemClock`. | Tests can control notification timestamps and all local time comes through one dependency. |
| G.3 | P1 | Open | Prefer storage-returned transition timestamps. | When storage owns the persisted timestamp, notification DTOs use the same value. |
| G.4 | P2 | Open | Add clock skew docs. | Multi-node deployments document database time vs application time expectations. |

## Phase H: Tests

Goal: prove notification behavior under failure and load.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| H.1 | P1 | Done | Add no-notify-on-storage-exception tests. | Storage failure never emits a success/failure UI event. |
| H.2 | P1 | Done | Add cancellable retry tests. | Shutdown cancellation exits notification retry promptly. |
| H.3 | P1 | Open | Add stats debounce tests. | Many state changes produce bounded stats notifications. |
| H.4 | P1 | Open | Add outbox atomicity tests. | SQL transition and outbox append commit or roll back together. |
| H.5 | P1 | Open | Add outbox replay tests. | Failed SignalR delivery is retried after dispatcher restart. |
| H.6 | P2 | Open | Add duplicate event tests. | Duplicate event delivery is harmless for the dashboard. |

## Suggested Implementation Order

1. Add tests for storage exceptions and notification suppression.
2. Make `SafeNotifyAsync` cancellable and use backoff with jitter.
3. Add stats notification debounce.
4. Add the typed `JobStateChangedEvent` model.
5. Add SQL outbox storage and write events with transitions.
6. Move SignalR delivery into a background outbox dispatcher.
7. Add outbox metrics, cleanup, and replay tests.

## Non-Goals

- Do not make SignalR delivery part of job execution correctness.
- Do not block the high-priority worker and idempotency fixes on outbox work.
- Do not promise exactly-once UI delivery; aim for at-least-once delivery with
  idempotent dashboard handling.
- Do not add a general event bus before the local outbox contract is stable.
