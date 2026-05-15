# Delivery Guarantees

ChokaQ is designed around **at-least-once execution**.

This means a job that has been accepted by durable storage should either finish,
retry, or become visible for operator action in DLQ. It does not mean that
external side effects are exactly-once.

## What ChokaQ Guarantees

- SQL Server mode stores accepted work in `JobsHot` and uses ownership-aware
  state transitions when moving jobs to Archive, DLQ, or delayed retry.
- A stale worker that lost ownership should not be able to finalize a job owned
  by another worker.
- `Fetched` jobs that never started user code can be returned to `Pending` by
  recovery.
- `Processing` jobs with expired heartbeats move to DLQ as `Zombie` jobs for
  operator review.
- Retry attempts use configured limits and delayed scheduling.
- The Deck and storage APIs expose DLQ state so failed, cancelled, timed-out, or
  zombie jobs can be inspected and handled deliberately.

## What ChokaQ Does Not Guarantee

- ChokaQ does not guarantee exactly-once side effects.
- ChokaQ does not know whether a handler sent an email, charged a card, wrote to
  another database, or called a third-party API before a crash or timeout.
- ChokaQ does not automatically retry `Processing` zombies, because retrying a
  started job may duplicate external side effects.
- ChokaQ does not promise strict FIFO ordering unless a specific queue policy and
  test explicitly state that behavior.
- In-memory mode is not durable. Accepted in-memory work is process-local and is
  lost when the process exits.

## Delayed Execution

A delayed job is work that ChokaQ has already accepted, stored, and promised to
remember, but that workers are not allowed to run yet.

This is different from putting `Task.Delay(...)` inside your application. With
`Task.Delay`, the wait usually lives inside one process. If that process exits,
the timer disappears with it. In SQL Server mode, ChokaQ stores the job in
`JobsHot` with a future `ScheduledAtUtc` value. The schedule is data, not an
in-memory timer.

The durable flow is:

1. Your application calls `EnqueueAsync(..., delay: someTimeSpan)`.
2. ChokaQ writes the job to SQL Server immediately.
3. The row stays in `JobsHot` with `ScheduledAtUtc` set to a future UTC time.
4. Workers skip that row while `ScheduledAtUtc` is still in the future.
5. When the scheduled time arrives, the same normal worker fetch path can claim
   the job and process it.

The important part is that the delayed job already exists before it is eligible.
If the host restarts during the waiting period, the job is still in SQL. If one
worker dies before the scheduled time, another worker can pick it up later.

Delayed execution is also the same basic idea used for retries. A transient
failure does not need a sleeping thread. ChokaQ keeps the job in `JobsHot`,
calculates the next due time, stores that time, and lets workers ignore the job
until it is due.

Queue lag intentionally ignores future-due work until it becomes eligible. A job
scheduled for 30 seconds from now should not make a queue look unhealthy just
because it has been stored for 29 seconds. Once it is due, lag is measured from
the due time, because that is when the job became actionable.

## Handler Idempotency Checklist

Treat every handler that performs external side effects as idempotent work.

Use one or more of these patterns:

- A stable business idempotency key, such as order ID, payment attempt ID,
  invoice ID, webhook event ID, or email campaign recipient ID.
- A unique constraint in the system that owns the side effect.
- An outbox table when a local database update and a later external publish must
  be coordinated.
- A provider-supported idempotency key for payment, email, webhook, or API calls.
- A "read before write" or "upsert" flow where repeating the handler converges
  to the same final state.
- Clear compensation or manual review for side effects that cannot be made
  idempotent.

Idempotency TTL is a business decision. A payment key may need longer retention
than a notification key. Pick TTLs based on how long duplicates can realistically
arrive and how expensive it is to keep the marker.

The optional idempotency middleware is claim-based for stores that implement
`IIdempotencyClaimStore`: it writes an `InProgress` claim before the handler
runs, skips concurrent duplicates, and writes a completion marker only after
successful handler execution. This reduces duplicate handler execution for the
same active key, but it still does not make external systems exactly-once.

## Timeout, Cancellation, And Shutdown

`Execution.DefaultTimeout` is a handler execution boundary. If a handler exceeds
that boundary, ChokaQ cancels the handler token and finalizes the job through the
timeout path.

Admin cancellation is a different intent: an operator asked ChokaQ to stop or
remove work. Public docs and operator views should treat timeout and admin
cancellation as different outcomes.

Shutdown is cooperative. Workers can release `Fetched` jobs that have not
started user code. Started `Processing` jobs are not blindly returned to
`Pending`, because the handler may already have produced side effects. If the
process dies before finalization, recovery moves the expired `Processing` row to
DLQ as a zombie.

Shutdown budgets must line up from inside out:

- `Worker.ShutdownGracePeriod` bounds in-memory worker-loop shutdown.
- `SqlServer.WorkerShutdownGracePeriod` bounds SQL active processing drain.
- `.NET HostOptions.ShutdownTimeout` should be longer than the ChokaQ worker
  budget plus normal finalization latency.
- Kubernetes `terminationGracePeriodSeconds` should be longer than the host
  shutdown timeout.

State outcomes during shutdown are:

- `Pending` rows are untouched.
- `Fetched` rows that never started user code can be released to `Pending`.
- A cooperative `Processing` handler can observe cancellation and be
  rescheduled for retry.
- A `Processing` handler that completes inside the budget finalizes normally.
- A `Processing` handler that ignores cancellation can outlive the ChokaQ
  shutdown budget; if the host then kills the process, zombie recovery moves it
  to DLQ later.

## Retry Visibility And Ordering

Transient retries stay in `JobsHot` with a future `ScheduledAtUtc`. They are not
visible to workers again until that time is due. Retry delay is calculated from
`Retry.BaseDelay`, `Retry.BackoffMultiplier`, `Retry.MaxDelay`, optional
throttling `RetryAfter`, and jitter.

`Retry.MaxAttempts` bounds how many executions can start. `Retry.MaxJobAge`
bounds wall-clock lifetime. If the next retry would exceed the lifetime budget,
the job moves to DLQ with `RetryLifetimeExpired` instead of being scheduled
again.

SQL fetch order is priority first, then due scheduled time, then creation time
within the configured queue and worker-capacity constraints. This is a
best-effort scheduling contract, not strict FIFO. High-priority traffic can run
before older lower-priority work by design; use separate queues and queue
`MaxWorkers` when a workload needs isolation.

## In-Memory Mode Boundary

In-memory mode is for demos, tests, local development, and volatile workloads.
It uses process memory and `System.Threading.Channels`; it is not a durable
backlog. Use SQL Server mode for restart-safe production work.

The in-memory worker is channel-driven. A successful enqueue writes a Hot row
and then writes the job object into an in-process channel. The channel item is
the execution notification; the Hot row is the control/audit row used to reject
stale channel items. If the process exits after a Hot row is created but before
the channel item is drained, that orphaned Hot row is not recovered by a later
process restart. Use SQL Server mode when accepted work must survive restarts.

Before dispatching user code, the in-memory worker verifies that the Hot row is
still `Pending` and that its persisted type key and payload still match the
channel item. If an operator cancel, DLQ move, edit, resurrection, or duplicate
channel write changed the Hot row, the stale channel item is skipped instead of
executed.

Admin restart from DLQ resurrects the row into Hot and then attempts to write a
new item to the in-process channel. In-memory bulk restart logs requeue results
per category (`requeued`, missing Hot row, not Pending, unknown type, empty
payload, failed). A process crash between resurrection and channel requeue can
leave an orphaned Hot row; this is an accepted preview limitation of the
non-durable in-memory mode.

## Type Keys And Payload Compatibility

For Bus jobs, treat the profile `typeKey` as a persisted message-contract name.
Prefer stable semantic keys such as `email.send.v1` instead of CLR class names.

When changing a persisted job DTO:

- Additive fields are usually safer than renaming or removing fields.
- Breaking schema changes should use a new type key.
- Serializer option changes can affect old rows already stored in SQL or DLQ.
- Keep old handlers or migration guidance when old jobs may still exist.

## Operational Review

When a job lands in DLQ, inspect the failure reason before resurrecting it.
`Zombie`, `Timeout`, `Cancelled`, `FatalError`, `Transient`,
`RetryLifetimeExpired`, and `MaxRetriesExceeded` mean different things
operationally. A zombie should be resurrected only when the handler is
idempotent or the external side effect has been inspected.
