# Release Verification Matrix

This matrix is the preview gate for the high-priority hardening work. A build is
not release-ready until every required gate below passes or a listed limitation
is explicitly accepted in public docs.

## Required Commands

Run from the repository root:

```powershell
dotnet build ChokaQ.sln --no-restore
dotnet test ChokaQ.sln --no-restore --filter "FullyQualifiedName!~Integration"
dotnet test ChokaQ.sln --no-restore --filter "Category=Integration"
npm --prefix docs run docs:build
dotnet build samples\ChokaQ.Sample.NuGetLab\ChokaQ.Sample.NuGetLab.sln --configuration Release --no-restore
git diff --check
```

The integration gate requires SQL Server test infrastructure. Skipping it is a
release exception, not a pass.

## Preview Blocker Coverage

| Blocker | Required proof | Test / doc gate |
|---|---|---|
| Claim honesty | Docs state at-least-once, idempotent handlers, in-memory non-durability, retry visibility, type keys, payload compatibility. | `docs/delivery-guarantees.md`, `README.md`, `docs/configuration.md` |
| Type identity | Registered keys are stable, short-name collisions do not resolve silently, unknown requeue is explicit. | `JobTypeRegistryTests`, `JobTypeRegistryCollisionTests`, `BusJobDispatcherTests`, `JobWorkerTests`, `SqlChokaQQueueTests` |
| Serialization and limits | Shared serializer, configured payload behavior, metadata and payload rejection before storage mutation. | `SystemTextJsonChokaQJobSerializerTests`, `InMemoryQueueTests`, `SqlChokaQQueueTests`, `ChokaQOptionsTests` |
| Atomic idempotency | Active duplicates cannot execute concurrently; completed duplicates are skipped; failed claims are released; TTLs expire. | `IdempotencyMiddlewareTests` |
| Cancellation taxonomy | Admin cancellation and execution timeout persist different outcomes; cancellation race behavior is covered. | `JobProcessorTests`, `JobStateManagerTests`, `JobWorkerTests`, SQL integration state-transition tests |
| Heartbeat policy | Heartbeat write failures are separate telemetry and degraded by default. | `JobProcessorTests`, `ChokaQMetricsTests`, `docs/5-operations/heartbeat-pressure.md` |
| Shutdown semantics | SQL and in-memory workers have bounded shutdown/drain behavior. | `SqlJobWorkerTests`, `JobWorkerTests`, `docs/delivery-guarantees.md` |
| Circuit half-open | Half-open permits cannot leak; multiple probes close only after required successes; timeout safety exists. | `InMemoryCircuitBreakerTests`, `JobProcessorTests` |
| Retry lifetime | Retry count and wall-clock lifetime are enforced separately and have distinct DLQ reason. | `JobProcessorTests`, `ChokaQOptionsTests`, `docs/delivery-guarantees.md` |
| Zombie behavior | `Fetched` recovery and `Processing` zombie DLQ behavior are distinct. | `ZombieRescueServiceTests`, `InMemoryJobStorageTests`, `SqlJobStorageIntegrationTests`, `docs/2-lifecycle/zombie-rescue.md` |
| In-memory boundary | Channel-driven source of truth is documented; stale channel items skip execution; restart/requeue reporting is deterministic. | `JobWorkerTests`, `docs/delivery-guarantees.md`, `docs/configuration.md` |
| Observability | Operators can distinguish handler failures, heartbeat pressure, state conflicts, idempotency outcomes, and circuit events. | `ChokaQMetricsTests`, operations runbooks, `docs/configuration.md` |
| Package consumer path | The top-level package restores into a separate app without source project references and runs against SQL Server. | `samples/ChokaQ.Sample.NuGetLab`, `docs/samples/nuget-lab.md`, local NuGet smoke gate |

## Required Test Groups

Use these focused filters when validating a specific risk before the full gate:

| Area | Focused command |
|---|---|
| Type identity | `dotnet test ChokaQ.sln --no-restore --filter "FullyQualifiedName~JobTypeRegistry|FullyQualifiedName~BusJobDispatcher|FullyQualifiedName~JobWorkerTests"` |
| Serialization and boundaries | `dotnet test ChokaQ.sln --no-restore --filter "FullyQualifiedName~SystemTextJsonChokaQJobSerializerTests|FullyQualifiedName~InMemoryQueueTests|FullyQualifiedName~SqlChokaQQueueTests|FullyQualifiedName~ChokaQOptionsTests"` |
| Idempotency | `dotnet test ChokaQ.sln --no-restore --filter "FullyQualifiedName~IdempotencyMiddlewareTests"` |
| Cancellation, timeout, heartbeat | `dotnet test ChokaQ.sln --no-restore --filter "FullyQualifiedName~JobProcessorTests|FullyQualifiedName~JobStateManagerTests|FullyQualifiedName~JobWorkerTests"` |
| Circuit and recovery | `dotnet test ChokaQ.sln --no-restore --filter "FullyQualifiedName~InMemoryCircuitBreakerTests|FullyQualifiedName~ZombieRescueServiceTests|FullyQualifiedName~InMemoryJobStorageTests"` |
| Retry and ordering | `dotnet test ChokaQ.sln --no-restore --filter "FullyQualifiedName~JobProcessorTests|FullyQualifiedName~QueriesReadConsistencyTests|FullyQualifiedName~InMemoryJobStorageTests"` |
| In-memory mode | `dotnet test ChokaQ.sln --no-restore --filter "FullyQualifiedName~JobWorkerTests|FullyQualifiedName~InMemoryQueueTests|FullyQualifiedName~InMemoryJobStorageTests"` |
| Observability | `dotnet test ChokaQ.sln --no-restore --filter "FullyQualifiedName~ChokaQMetricsTests|FullyQualifiedName~ChokaQLogEventsTests"` |

## State Transition Coverage

| Transition | Expected behavior | Coverage |
|---|---|---|
| `Pending -> Fetched` | Workers claim only due, active, unpaused work within queue limits. | `InMemoryJobStorageTests`, `SqlJobStorageIntegrationTests`, `SqlConcurrencyTests` |
| `Fetched -> Processing` | Execution starts only if the worker still owns the row. | `JobStateManagerTests`, `SqlConcurrencyTests`, `SqlJobWorkerTests` |
| `Fetched -> Pending` | Unstarted work can be released by shutdown, pause, or abandoned-fetch recovery. | `InMemoryJobStorageTests`, `SqlJobWorkerTests`, `ZombieRescueServiceTests` |
| `Processing -> Archive` | Successful jobs move to Archive through worker-owned finalization. | `JobProcessorTests`, `InMemoryJobStorageTests`, `SqlJobStorageIntegrationTests` |
| `Processing -> Pending` | Cooperative transient retry reschedules with delay and attempt count. | `JobProcessorTests`, storage integration tests |
| `Processing -> DLQ` | fatal, timeout, cancellation, retry exhaustion, retry lifetime expiry, and zombie paths preserve reason. | `JobProcessorTests`, `InMemoryJobStorageTests`, `SqlJobStorageIntegrationTests` |
| `DLQ -> Hot` | Resurrection is atomic at storage boundary; in-memory channel requeue is best-effort and reported. | `SqlJobStorageBulkTests`, `JobWorkerTests`, `ChokaQHubTests` |
| `Hot -> DLQ` admin cancel | Pending/Fetched jobs can be cancelled without running user code. | `JobWorkerTests`, `ChokaQHubTests`, storage tests |
| stale buffered copy -> no-op | Lost ownership or stale channel data must not dispatch/finalize user code. | `JobProcessorTests`, `SqlConcurrencyTests`, `JobWorkerTests` |

## Accepted Preview Limitations

These are not release blockers because the public claim is deliberately lower:

| Limitation | Public location |
|---|---|
| In-memory mode is not durable and can leave orphaned Hot rows after process crash. | `docs/delivery-guarantees.md`, `docs/configuration.md` |
| In-memory restart can resurrect a row before channel requeue; failures are logged/reported, not recovered across restart. | `docs/delivery-guarantees.md` |
| Adaptive worker autoscaling is guidance only, not built-in control-loop behavior. | `docs/5-operations/autoscaling.md` |
| Global throughput throttles and jobs-per-second limits are post-preview hardening. | `docs/3-deep-dives/backpressure-policy.md`, roadmap |
| Typed idempotency result replay is not provided; middleware stores completion markers. | `docs/delivery-guarantees.md`, `docs/5-operations/idempotent-handlers.md` |
| `IRetryStrategy` and per-queue retry lifetime override are not public preview APIs. | `docs/configuration.md`, roadmap |

## Sign-Off Rule

For a preview release candidate, record the exact command output in the release
sign-off record from [Release Checklist](/release-checklist). If any required
gate is skipped, write the reason and the accepted risk before tagging or
packing artifacts.
