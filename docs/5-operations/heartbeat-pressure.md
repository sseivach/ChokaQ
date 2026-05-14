# Heartbeat Pressure Runbook

Per-job heartbeats prove that a `Processing` row is still owned by a live
handler. Heartbeat failures are not automatically handler failures: they often
mean SQL pressure, network latency, locks, or host overload.

## Primary Signals

Use these signals together:

| Signal | Meaning |
|---|---|
| `chokaq.jobs.heartbeat_failures` | Heartbeat writes failed for running jobs. |
| `JobHeartbeatDegraded` logs | A job crossed `Execution.HeartbeatFailureThreshold`. |
| `chokaq.jobs.state_transition_conflicts` | A worker-owned update affected no rows, often because ownership changed. |
| SQL wait stats / deadlocks | Storage pressure may be blocking heartbeat writes. |
| Queue lag | Rising lag means workers are not keeping up or are blocked. |

## Checks

1. Confirm whether failures are broad or isolated to one queue/job type.
2. Check SQL connectivity, command timeout, deadlocks, blocking sessions, and
   transaction-log pressure.
3. Check CPU/thread-pool pressure on the worker host.
4. Compare handler duration with `Execution.DefaultTimeout` and queue-specific
   timeout overrides.
5. Inspect whether queue `MaxWorkers` is too high for the downstream dependency.
6. If `Execution.CancelOnHeartbeatFailure` is `true`, verify that fail-fast
   cancellation is intentional for this environment.

## Response

- For transient SQL/network pressure, keep `CancelOnHeartbeatFailure = false`
  and let degraded telemetry plus zombie recovery make the final decision.
- For persistent storage pressure, reduce worker concurrency, lower queue
  `MaxWorkers`, or scale SQL resources before raising heartbeat thresholds.
- For a single slow handler type, increase the queue execution timeout only if
  the long runtime is expected and idempotent.
- If processing zombies appear, inspect side effects before resurrecting them.

## Defaults

`Execution.HeartbeatFailureThreshold` defaults to `10`, and
`Execution.CancelOnHeartbeatFailure` defaults to `false`.
