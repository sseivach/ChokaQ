# SQL Storage Scale Hardening Roadmap

This backlog keeps SQL Server storage work that is useful before a stable or
high-throughput production claim, but is not a blocker for the first NuGet
preview.

The preview baseline is now acceptable:

- Hot to Archive, Hot to DLQ, DLQ to Hot, retry, zombie rescue, cancellation,
  and cleanup paths are transaction-scoped.
- Worker ownership guards protect stale finalization.
- SQL fetch uses `UPDLOCK` and `READPAST`, with committed active counts for
  queue bulkhead decisions.
- Prefetched shutdown release is bounded.
- Idempotency middleware receives the job execution cancellation token.
- `NOLOCK` is guarded by tests and limited to passive dashboard telemetry.
- Parameter merge no longer uses reflection.
- `ParameterBuilder` caches public parameter property metadata.
- Integration tests cover concurrent success/failure finalization and queue
  bulkhead behavior under competing queues.

## Remaining Stable-Release Work

| ID | Priority | Status | Work Item | Notes |
|---|---|---|---|---|
| SQL-S.1 | P2 | Open | Add transaction rollback fault-injection tests. | Prove simulated failures inside archive, DLQ, and resurrection transactions do not lose or duplicate rows. |
| SQL-S.2 | P2 | Open | Replace large selected-ID JSON batches. | Keep current `OPENJSON` path for normal batches, but add TVP or temp-table flow for very large selected operations. |
| SQL-S.3 | P2 | Open | Add 100k selected-ID benchmark. | Measure purge, resurrection, and cancellation with the optimized ID path. |
| SQL-S.4 | P2 | Open | Audit and replace high-concurrency `MERGE` where justified. | Current tests cover concurrency; replace with explicit update-then-insert only if production load or SQL Server edge cases justify the complexity. |
| SQL-S.5 | P2 | Open | Add compiled row materializers. | `TypeMapper` caches metadata today; compiled delegates can reduce reflection cost on high-volume read paths. |
| SQL-S.6 | P2 | Open | Cache column ordinals per result shape. | Avoid repeated column/property matching while materializing many rows. |
| SQL-S.7 | P2 | Open | Encapsulate SQL template replacement. | Current values are parameterized; the remaining risk is placeholder fragility and maintainability. |
| SQL-S.8 | P2 | Open | Add connection-pool pressure telemetry or benchmark. | Open-per-method relies on ADO.NET pooling and is acceptable until measured otherwise. |
| SQL-S.9 | P3 | Open | Split `SqlJobStorage` internally. | Preserve one public `IJobStorage` facade while extracting command, transition, admin, read, queue, and maintenance stores. |
| SQL-S.10 | P3 | Open | Optimize enqueue idempotency roundtrips. | Correct today; benchmark insert-first recovery before adding SQL complexity. |

## Release Position

Do not block the preview package on this file. Use it when moving from preview
readiness to stronger stable-production claims.
