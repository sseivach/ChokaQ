# Worker And Processor Hardening Roadmap

This file tracks hardening work for ChokaQ's worker loops and `JobProcessor`.
It is intentionally private and not linked from the public documentation
navigation.

The audit is partly outdated. SQL mode no longer uses a dangerous dual-queue
model for production execution. `SqlJobWorker` fetches from durable SQL storage
with `FetchNextBatchAsync`, then uses a bounded in-process channel only as a
prefetch buffer. SQL remains the source of truth.

The remaining risks are concentrated in the in-memory worker mode, cancellation
edge cases, heartbeat policy, serializer/type fallback behavior, and the public
idempotency contract around at-least-once execution.

The newest worker/processor audit also calls out active-token lifecycle,
retry visibility semantics, manual worker scaling, and circuit key granularity.
Some points are already solved in SQL mode or in `JobProcessor`; the remaining
work is mostly about edge-case guarantees, tests, and public contracts.

The latest worker audit repeats an important source-of-truth warning. In the
current codebase that warning is no longer accurate for SQL mode, but it is
still accurate for in-memory mode: the in-memory worker consumes from a channel
while also checking and mutating storage. That mode must either be clearly
documented as non-durable development behavior or redesigned to fetch from
storage the same way SQL mode does.

## Current State

Strong parts already implemented:

- SQL mode uses `SqlChokaQQueue`, so durable enqueue writes to SQL only.
- SQL workers poll storage with `FetchNextBatchAsync`.
- SQL workers use a bounded channel as a prefetch buffer, not as durable state.
- SQL workers track processing tasks and wait for them during shutdown.
- SQL workers release prefetched jobs on pause and shutdown paths.
- SQL shutdown distinguishes prefetched-but-unstarted work from processing work:
  unstarted `Fetched` rows can be released to `Pending`, while `Processing`
  rows must not be blindly returned to `Pending` because user code may already
  have produced external side effects.
- `JobProcessor` records queue lag as the primary saturation signal.
- `JobProcessor` uses `MarkAsProcessing` as the final ownership gate before
  user code runs.
- Circuit breaker rejection happens before handler execution.
- Handler execution has timeout, heartbeat, retry, jitter, DLQ, and failure
  taxonomy support.
- Execution timeout is configurable through ChokaQ options and queue-level
  overrides; it is no longer a hardcoded 15-minute value.
- Stale finalization is blocked at the storage layer through `WorkerId` guards.
- SQL mode does not reserialize job payloads before execution; it passes the
  stored payload from `JobsHot` into `JobProcessor`.

Remaining gaps:

- In-memory worker mode is channel-driven while also writing to storage, which
  can create orphaned Hot rows if the channel is lost or resurrection requeue
  fails.
- In-memory worker can read a stale channel item and then discover that storage
  state has already changed.
- In-memory worker serializes the in-memory job object before processing instead
  of using the stored payload.
- In-memory worker processes one channel job at a time and does one storage
  lookup per job.
- In-memory worker fetches all queue settings per job to check pause state.
- In-memory restart can resurrect a job and then fail or duplicate the volatile
  channel requeue step.
- In-memory worker starts loop tasks with `Task.Run` and does not currently
  await all worker loop tasks during `StopAsync`.
- SQL active processing drain is observed, but ChokaQ does not yet expose its
  own explicit shutdown grace budget separate from the host's shutdown timeout.
- SQL active processing drain currently waits for tracked tasks; if a handler
  ignores cancellation, final behavior depends on host shutdown timeout and
  Kubernetes/container termination settings.
- Shutdown semantics need clearer public docs: `Fetched` jobs can safely return
  to `Pending`; `Processing` jobs either finish, cooperatively cancel and
  reschedule, or later become Processing zombies/DLQ candidates if the process
  is killed.
- `CancelJob` can miss a job before `_activeJobTokens.TryAdd` runs.
- `_activeJobTokens` is cleaned up on normal processor exit, but reused job IDs,
  pending cancellations, and catastrophic termination boundaries need explicit
  tests so ghost cancellation cannot hide in edge cases.
- Heartbeat write failures still cancel execution after a default threshold of
  3.
- Timeout and admin cancellation are conflated in the same `OperationCanceled`
  path.
- Serialization is still direct `JsonSerializer.Serialize(...)` without shared
  ChokaQ options.
- Some type resolution fallback paths still use short CLR names or full
  AppDomain scans.
- Idempotency is optional, while at-least-once execution means side-effecting
  handlers need an explicit policy.
- Retry scheduling has jitter and durable state, but visibility-timeout
  semantics, ordering expectations, dedup boundaries, and starvation behavior
  need clearer docs and tests.
- Worker count is manually controlled; queue lag exists as a scaling signal, but
  there is no adaptive worker controller.
- Circuit breaker keys are currently job-type oriented; dependency-specific or
  queue-specific circuit keys are tracked in the resilience roadmap.

## Design Principles

- Durable SQL mode must have one source of truth: `JobsHot`.
- A channel in SQL mode is only a bounded local prefetch buffer.
- In-memory mode must be clearly scoped as development or non-durable mode, or
  it must also become storage-driven.
- Worker shutdown must be observed, bounded, and deterministic.
- Shutdown must never pretend that started user code is side-effect-free.
  Returning `Processing` jobs to `Pending` is only safe when the handler
  cooperatively cancelled before doing irreversible work or is externally
  idempotent.
- Host-level shutdown timeout, Kubernetes `terminationGracePeriodSeconds`, and
  ChokaQ's execution/shutdown budgets must be documented together.
- Admin cancellation must not rely only on an in-memory token lookup.
- Heartbeat failures are storage/network pressure signals before they are proof
  that user code should be killed.
- At-least-once execution must be treated as a public contract, not hidden in
  comments.
- Serialization and type identity are persistence contracts.

## Status Legend

| Status | Meaning |
|---|---|
| Done | Implemented, tested, and documented for current scope. |
| Partial | Some behavior exists, but the acceptance criteria below are not complete. |
| Open | Not implemented yet. |
| Deferred | Intentionally postponed until higher-risk items are complete. |

## Executive Sequence

1. Preserve the SQL worker source-of-truth model.
2. Decide the future of in-memory worker mode: channel-only dev mode or
   storage-driven worker loop.
3. Harden admin cancellation so cancel requests are not lost before token
   registration.
4. Make heartbeat failure behavior less destructive.
5. Split timeout and admin cancellation taxonomy.
6. Make idempotency requirements configurable and visible.
7. Define and document shutdown grace behavior for SQL and in-memory modes.
8. Centralize serialization and remove unsafe type fallbacks.
9. Add shutdown, cancellation, pause, resurrection, and race tests.

## Latest Audit Classification

| Finding | Current Status | Action |
|---|---|---|
| SQL worker is channel-driven instead of storage-driven. | Outdated. SQL mode uses `FetchNextBatchAsync`; channel is only a bounded prefetch buffer. | Preserve current SQL design. |
| Worker bypasses SQL priority, scheduling, pause, and `MaxWorkers`. | Outdated for SQL mode. These are enforced in SQL fetch. | Keep SQL fetch as the production worker path. |
| Circuit breaker is not integrated. | Outdated. `JobProcessor` checks the circuit before dispatch. | Track remaining permit-lifecycle work in the resilience roadmap. |
| No per-job timeout. | Outdated. `JobProcessor` applies configurable execution timeout. | Split timeout from admin cancellation and keep timeout docs current. |
| Channel and storage can diverge. | Still valid for in-memory mode. | Redesign or explicitly scope in-memory mode. |
| Payload is reserialized from memory. | Still valid for in-memory mode. | Use stored payload or make channel-only semantics explicit. |
| Queue pause check is one storage read per job. | Still valid for in-memory mode. | Cache queue settings or use storage-driven fetch. |
| Restart can resurrect then fail to enqueue in channel. | Still valid for in-memory mode. | Use storage-driven polling or document/test non-durable semantics. |
| `Type.GetType(job.Type)` is unsafe as a production lookup. | Valid for fallback paths. Registered jobs and compiled dispatch already avoid the worst path, but fallback behavior still needs hardening. | Track strict registry mode and fallback removal in the type-resolution roadmap. |
| `_activeJobTokens` can leak or ghost-cancel. | Partially valid. Normal processor exit removes and disposes tokens, but missed pre-registration cancels, reused IDs, and catastrophic boundaries need tests. | Add pending-cancel handling and token lifecycle regression coverage. |
| Worker scaling is manual. | Valid. Queue lag is recorded, but there is no adaptive worker controller. | Document lag-based scaling now; consider adaptive scaling later. |
| Retry scheduling lacks visibility-window, ordering, and dedup contracts. | Partially valid. SQL has scheduled fetch, lease states, and zombie recovery, but public semantics and starvation tests are missing. | Document visibility-timeout equivalent and add retry/starvation tests. |
| Execution timeout is hardcoded to 15 minutes. | Outdated. Timeout is configurable globally and per queue. | Keep timeout configuration documented and split timeout from admin cancel taxonomy. |
| Circuit breaker should be per queue or dependency. | Valid enhancement. Current keying is job-type oriented. | Track dependency-specific circuit keys in the resilience roadmap. |
| Ctrl+C/Kubernetes shutdown can kill work mid-handler. | Valid risk, partly mitigated. SQL mode releases unstarted prefetched jobs and waits for active processing tasks, but there is no explicit ChokaQ shutdown grace budget or public Kubernetes guidance yet. | Keep as high-priority contract/test work before stronger production claims. |
| Shutdown should return jobs to Pending. | Only valid for unstarted `Fetched` jobs or cooperative cancellation before irreversible side effects. Blindly returning `Processing` to `Pending` can duplicate external side effects. | Document `Fetched` release vs `Processing` shutdown semantics and tie this to idempotency guidance. |
| Worker should finish or cancel within 30 seconds. | Valid target shape, but the number must be configurable and aligned with host/container shutdown timeout. | Add a shutdown budget option or documented HostOptions/Kubernetes configuration path. |

## Phase A: Source Of Truth

Goal: make each worker mode have exactly one authoritative queue boundary.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| A.1 | P0 | Done | Make SQL enqueue write only to SQL. | SQL mode uses `SqlChokaQQueue` and does not push durable work into `InMemoryQueue`. |
| A.2 | P0 | Done | Make SQL worker storage-driven. | SQL mode fetches with `FetchNextBatchAsync` from `JobsHot`. |
| A.3 | P0 | Done | Keep SQL channel as prefetch only. | SQL channel is bounded, local, and refilled only from fetched SQL rows. |
| A.4 | P1 | Partial | Clarify in-memory mode source of truth. | In-memory mode uses both channel and in-memory storage; docs should state this is non-durable/dev-mode behavior. |
| A.5 | P1 | Open | Decide in-memory worker redesign. | Either make in-memory worker storage-driven through `FetchNextBatchAsync` or explicitly document channel-only semantics. |
| A.6 | P1 | Open | Add orphaned Hot row recovery test for in-memory mode. | If a job exists in in-memory Hot storage but not the channel, behavior is documented and tested. |
| A.7 | P1 | Open | Add stale channel item test for in-memory mode. | If a channel item exists after storage moved/cancelled the job, the worker skips it without executing stale payload. |
| A.8 | P1 | Open | Stop reserializing stored jobs in in-memory worker. | In-memory processing either uses `storageJob.Payload` or stops pretending storage is the source of truth in that mode. |

Audit note:

The "dual queue" criticism is valid as a risk model, but it no longer describes
SQL production mode. It mainly applies to in-memory mode and to recovery flows
that manually requeue into the channel after storage mutation.

## Phase B: Batch Fetch And Prefetch

Goal: avoid one job equals one storage roundtrip in production worker paths.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| B.1 | P0 | Done | Use SQL batch fetch. | `SqlJobWorker` calls `FetchNextBatchAsync` and writes fetched rows into a bounded buffer. |
| B.2 | P0 | Done | Respect queue pause before execution. | SQL processor loop refreshes queue pause state before starting prefetched jobs. |
| B.3 | P0 | Done | Release prefetched SQL jobs. | Paused or shutdown-prefetched jobs are released back to Pending. |
| B.4 | P1 | Partial | Avoid per-job storage lookup in in-memory mode. | In-memory worker still calls `GetJobAsync` per channel item. |
| B.5 | P1 | Open | Make in-memory worker batch-aware or declare it low-throughput. | In-memory mode either uses `FetchNextBatchAsync` or docs state its performance boundary. |
| B.6 | P2 | Open | Add prefetch sizing knobs. | SQL prefetch capacity and fetch batch size are configurable and documented. |
| B.7 | P2 | Open | Add prefetch metrics. | Buffer depth, fetched count, released count, and fetch latency are observable. |
| B.8 | P1 | Open | Remove per-job queue settings read in in-memory mode. | Pause decisions use cached queue metadata or storage-driven fetch instead of `GetQueuesAsync` per job. |

## Phase C: Worker Shutdown

Goal: ensure worker loops and processing tasks stop predictably.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| C.1 | P0 | Done | Await SQL fetcher and processor loops. | `SqlJobWorker.ExecuteAsync` awaits real loop tasks. |
| C.2 | P0 | Done | Drain SQL processing tasks. | SQL worker waits for tracked processing tasks before reporting stopped. |
| C.3 | P1 | Partial | Track in-memory worker tasks. | In-memory worker stores loop tasks, but `StopAsync` does not await them. |
| C.4 | P1 | Open | Await in-memory worker shutdown. | `StopAsync` cancels loop tokens and waits for all worker tasks with a bounded timeout. |
| C.5 | P1 | Open | Bound shutdown release operations. | Prefetched release uses a short cleanup timeout instead of unbounded `CancellationToken.None`. |
| C.6 | P1 | Partial | Reschedule cooperative shutdown cancellation. | `JobProcessor` reschedules when handler observes worker shutdown cancellation, but docs/tests must define this path. |
| C.7 | P1 | Open | Add explicit shutdown grace budget. | SQL and in-memory workers have a documented bounded window to drain active processing before host termination wins. |
| C.8 | P1 | Open | Bound SQL processing drain. | SQL worker logs and exits according to a configurable or documented budget instead of waiting indefinitely for handlers that ignore cancellation. |
| C.9 | P1 | Open | Document host shutdown alignment. | Docs explain `.NET HostOptions.ShutdownTimeout`, Kubernetes `terminationGracePeriodSeconds`, and ChokaQ execution timeout interactions. |
| C.10 | P1 | Open | Document shutdown state outcomes. | Docs distinguish `Fetched -> Pending`, cooperative `Processing -> Pending retry`, completed `Processing -> Archive/DLQ`, and killed `Processing -> Zombie/DLQ`. |
| C.11 | P1 | Open | Add shutdown tests. | Tests cover shutdown while idle, fetched, processing, paused, cooperative cancellation, and cancellation ignored by handler. |

Target shutdown semantics:

```text
Pending in storage      -> untouched
Fetched, not executing  -> release to Pending immediately
Processing, handler completes inside grace window -> Archive/DLQ/retry normally
Processing, handler observes shutdown cancellation -> reschedule according to policy
Processing, process killed before finalization -> zombie recovery moves to DLQ
```

Important contract:

Processing work is not automatically returned to `Pending` on hard process
termination. Once user code has started, ChokaQ cannot know whether external
side effects already happened. Idempotency remains required for jobs that call
payment providers, send emails, mutate third-party systems, or perform any
irreversible operation.

## Phase D: Cancellation Semantics

Goal: make admin cancellation reliable and distinguish it from timeout.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| D.1 | P1 | Partial | Support in-memory cancellation token lookup. | `CancelJob` cancels active tokens if present, but misses jobs before token registration. |
| D.2 | P1 | Open | Add pending cancellation registry. | Cancel requests made before token registration are remembered and applied when processing starts. |
| D.3 | P1 | Open | Register cancelability before or during execution lease acquisition. | Race window between `MarkAsProcessing` and `_activeJobTokens.TryAdd` is closed or covered by pending cancel. |
| D.4 | P1 | Partial | Persist cancellation with ownership guard. | Storage guards exist, but admin cancellation and timeout share taxonomy in processor catch path. |
| D.5 | P1 | Open | Split admin cancellation from timeout. | Admin cancel writes `Cancelled`; execution timeout writes `Timeout`. |
| D.6 | P1 | Open | Guard token lifecycle against reused IDs. | A stale cancellation token cannot affect a later execution that reuses the same job ID after restart or resurrection. |
| D.7 | P1 | Open | Add cancellation race tests. | Tests cover cancel before token add, cancel during handler, cancel after success, cancel during finalization, and reused-ID ghost cancellation. |

Possible implementation:

```csharp
private readonly ConcurrentDictionary<string, byte> _pendingCancels = new();

public void CancelJob(string jobId)
{
    _pendingCancels.TryAdd(jobId, 0);
    if (_activeJobTokens.TryGetValue(jobId, out var cts))
        cts.Cancel();
}
```

Then processing checks and consumes `_pendingCancels` immediately after token
registration.

## Phase E: Heartbeat Policy

Goal: treat heartbeat failures as pressure signals before killing useful work.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| E.1 | P1 | Partial | Make heartbeat threshold configurable. | `Execution.HeartbeatFailureThreshold` exists and is validated. |
| E.2 | P1 | Open | Raise production default. | Default moves from 3 to a less aggressive value or docs explain why 3 is safe. |
| E.3 | P1 | Open | Add degraded heartbeat state. | Repeated heartbeat failures can mark the job/worker as degraded instead of immediately cancelling the handler. |
| E.4 | P1 | Open | Add heartbeat failure metrics. | Operators can see heartbeat write failures separately from handler failures. |
| E.5 | P2 | Open | Add heartbeat pressure runbook. | Runbook maps failures to SQL latency, network, locks, command timeout, or overload. |

Recommended direction:

- Keep zombie rescue as the final abandoned-job authority.
- Log and expose heartbeat degradation before cancelling execution.
- Cancel only when configured policy says storage loss makes continued handler
  execution more dangerous than retry.

## Phase F: At-Least-Once And Idempotency Policy

Goal: make duplicate side-effect risk explicit and enforceable.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| F.1 | P0 | Done | Document at-least-once in processor comments. | Processor explains crash after handler before archive can cause duplicate execution. |
| F.2 | P1 | Partial | Document idempotency publicly. | Docs mention idempotency, but README should include a clear delivery-guarantees section. |
| F.3 | P1 | Open | Add idempotency enforcement policy. | Hosts can require `IIdempotentJob` or explicit opt-out for selected queues/job types. |
| F.4 | P1 | Open | Warn on non-idempotent side-effect jobs. | Optional diagnostics warn when registered job types do not provide idempotency metadata. |
| F.5 | P1 | Open | Connect to claim-based idempotency roadmap. | Optional idempotency middleware supports atomic execution claim, not only completed marker. |
| F.6 | P2 | Open | Add idempotent handler checklist. | Docs show safe external side-effect patterns. |

Important contract:

> Storage guards prevent stale workers from falsely finalizing jobs. They do not
> make external side effects exactly once. Handlers must be idempotent when
> duplicate side effects matter.

## Phase G: Serialization And Type Identity

Goal: make persisted payload and type identity explicit contracts.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| G.1 | P1 | Partial | Use `JobTypeRegistry` for registered jobs. | SQL, in-memory enqueue, dispatch, and requeue try registry first. |
| G.2 | P1 | Open | Remove short-name type fallback. | Unregistered jobs no longer persist or resolve only by `job.GetType().Name`. |
| G.3 | P1 | Open | Remove AppDomain scan from requeue path. | In-memory `RequeueJobFromStorage` does not scan all loaded types. |
| G.4 | P1 | Open | Centralize JSON options. | Queue enqueue, dispatcher, idempotency, and requeue use a shared serializer contract. |
| G.5 | P2 | Open | Add payload compatibility docs. | Docs cover versioned type keys, DTO evolution, and serializer option changes. |

This phase links directly to:

- `private_docs/high/type-resolution-hardening-roadmap.md`
- `private_docs/high/core-execution-hardening-roadmap.md`

## Phase H: Admin Restart And Requeue

Goal: avoid recovery flows that mutate storage and then depend on a volatile
channel step.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| H.1 | P0 | Done | SQL restart is storage-only. | `SqlJobWorker.RestartJobAsync` resurrects to Hot and lets the fetcher pick it up. |
| H.2 | P1 | Partial | In-memory restart requeues after resurrection. | In-memory mode mutates storage and then manually pushes to the channel. |
| H.3 | P1 | Open | Make in-memory restart atomic at its mode boundary. | Either restart uses storage-driven polling or resurrection and channel write are documented/test-covered as non-durable. |
| H.4 | P1 | Open | Add bulk restart partial-result reporting. | Operator can see resurrected, requeued, skipped, and failed counts in in-memory mode. |
| H.5 | P2 | Open | Add recovery tests. | Tests cover crash/failure between resurrection and channel requeue. |
| H.6 | P1 | Open | Prevent duplicate channel enqueue on restart. | If in-memory mode keeps channel requeue, restart paths dedupe or verify the job is not already queued. |

## Phase I: Scaling And Backpressure

Goal: make worker scaling predictable under load.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| I.1 | P1 | Done | Use SQL prefetch backpressure. | SQL fetcher stops claiming more rows when local buffer is full. |
| I.2 | P1 | Done | Use dynamic concurrency limiter. | SQL processor limits concurrently executing jobs. |
| I.3 | P1 | Partial | Expose queue lag for scaling. | Queue lag metrics exist; scaling recipes should be documented. |
| I.4 | P2 | Open | Add worker autoscaling guidance. | Docs explain scaling by lag, failure rate, worker heartbeat, and SQL bottlenecks. |
| I.5 | P2 | Open | Add worker pressure metrics. | Metrics expose prefetch buffer depth, permits, fetch latency, and release counts. |
| I.6 | P2 | Open | Define adaptive worker controller inputs. | Queue lag, worker utilization, failure rate, circuit state, CPU, and SQL latency are identified as scale signals. |
| I.7 | P2 | Deferred | Add adaptive worker controller. | Worker count can scale up/down automatically within configured min/max bounds. |
| I.8 | P2 | Deferred | Add Kubernetes/KEDA examples. | Only after SLO and alert docs are stable. |

## Phase J: Retry Visibility And Starvation

Goal: make retry and lease semantics clear enough to reason about under load.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| J.1 | P1 | Partial | Preserve durable scheduling. | Retry paths write durable `ScheduledAt` state and include jitter. |
| J.2 | P1 | Open | Document visibility-timeout equivalent. | Docs explain fetched timeout, processing heartbeat, zombie timeout, and how jobs become visible again or move to DLQ. |
| J.3 | P1 | Open | Define retry ordering expectations. | Public docs state whether ChokaQ promises FIFO, priority order, scheduled-time order, or best-effort execution. |
| J.4 | P1 | Open | Add starvation tests. | Older scheduled work cannot be indefinitely starved by newer high-volume traffic unless queue policy explicitly allows it. |
| J.5 | P1 | Open | Add retry storm tests. | Many transient failures are smoothed by jitter, circuit breaker delay, and queue limits. |
| J.6 | P2 | Open | Add dedup boundary docs. | Docs explain where enqueue uniqueness/idempotency applies and where it does not. |

## Phase K: Tests

Goal: prove worker behavior under the failure modes operators care about.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| K.1 | P1 | Open | Add in-memory orphan test. | Job present in Hot but absent from channel has documented behavior. |
| K.2 | P1 | Open | Add in-memory shutdown drain test. | Worker loops are awaited and no tasks leak. |
| K.3 | P1 | Open | Add cancellation race tests. | Pending cancel registry or equivalent closes missed-cancel window. |
| K.4 | P1 | Open | Add heartbeat failure policy tests. | Transient heartbeat failures do not kill normal jobs unless configured policy requires it. |
| K.5 | P1 | Open | Add admin restart crash test. | Recovery between resurrection and requeue is deterministic per mode. |
| K.6 | P2 | Open | Add SQL worker prefetch tests. | Pause/shutdown while prefetched releases jobs and avoids stale execution. |
| K.7 | P1 | Open | Add retry visibility tests. | Fetched and processing timeout behavior matches the documented visibility model. |
| K.8 | P1 | Open | Add starvation and ordering tests. | Retry, priority, and scheduled-time behavior is stable under mixed workloads. |
| K.9 | P1 | Open | Add shutdown grace tests. | Active handlers that honor cancellation reschedule or finalize within the grace window. |
| K.10 | P1 | Open | Add ignored-cancellation shutdown test. | A handler that ignores cancellation does not silently return a started job to `Pending`; recovery path is documented and observable. |

## Suggested Implementation Order

1. Add README delivery-guarantees section for at-least-once and idempotency.
2. Add pending cancel registry or equivalent race fix.
3. Split timeout vs admin cancellation taxonomy.
4. Raise or redesign heartbeat failure policy.
5. Decide and document the in-memory worker source-of-truth model.
6. Add explicit shutdown grace semantics and Kubernetes/HostOptions guidance.
7. Await in-memory worker loop shutdown.
8. Add SQL shutdown tests for cooperative cancellation and ignored cancellation.
9. Remove unsafe type and serializer fallbacks.
10. Document retry visibility and ordering semantics.
11. Add worker race/shutdown/restart/retry tests.
12. Add worker pressure metrics and autoscaling guidance.

## Non-Goals

- Do not remove the SQL worker prefetch buffer; it is useful backpressure, not a
  second durable queue.
- Do not promise exactly-once side effects.
- Do not blindly move `Processing` jobs back to `Pending` during hard shutdown.
- Do not make result idempotency mandatory globally without an opt-out policy.
- Do not optimize in-memory mode as if it were the primary durable production
  worker unless the project explicitly changes that goal.
