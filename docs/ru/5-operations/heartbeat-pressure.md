# Heartbeat Pressure Runbook

Per-job heartbeats доказывают, что `Processing` row все еще owned by live handler. Heartbeat failures не являются автоматически handler failures: часто они означают SQL pressure, network latency, locks или host overload.

## Primary signals

Используйте эти signals вместе:

| Signal | Meaning |
|---|---|
| `chokaq.jobs.heartbeat_failures` | Heartbeat writes failed for running jobs. |
| `JobHeartbeatDegraded` logs | Job crossed `Execution.HeartbeatFailureThreshold`. |
| `chokaq.jobs.state_transition_conflicts` | Worker-owned update affected no rows, часто потому что ownership changed. |
| SQL wait stats / deadlocks | Storage pressure may be blocking heartbeat writes. |
| Queue lag | Rising lag means workers are not keeping up or are blocked. |

## Checks

1. Проверьте, failures broad или isolated to one queue/job type.
2. Проверьте SQL connectivity, command timeout, deadlocks, blocking sessions и transaction-log pressure.
3. Проверьте CPU/thread-pool pressure на worker host.
4. Сравните handler duration с `Execution.DefaultTimeout` и queue-specific timeout overrides.
5. Проверьте, не слишком ли высок `MaxWorkers` queue для downstream dependency.
6. Если `Execution.CancelOnHeartbeatFailure` = `true`, убедитесь, что fail-fast cancellation intentional для этого environment.

## Response

- При transient SQL/network pressure держите `CancelOnHeartbeatFailure = false` и дайте degraded telemetry плюс zombie recovery принять final decision.
- При persistent storage pressure уменьшите worker concurrency, lower queue `MaxWorkers` или scale SQL resources before raising heartbeat thresholds.
- Для single slow handler type увеличивайте queue execution timeout только если long runtime expected и idempotent.
- Если появляются processing zombies, inspect side effects перед resurrection.

## Defaults

`Execution.HeartbeatFailureThreshold` defaults to `10`, а `Execution.CancelOnHeartbeatFailure` defaults to `false`.
