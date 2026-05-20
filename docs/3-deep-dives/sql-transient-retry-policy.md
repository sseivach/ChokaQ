# SQL Transient Retry Policy

![SQL transient retry policy](/diagrams/49-sql-transient-retry-policy.png)

SQL transient retry policy handles short-lived SQL failures such as deadlocks,
timeouts, service busy responses, and temporary database unavailability.

This policy is for ChokaQ's own storage operations. It is not the same as job
handler retry.

## Where It Lives

`SqlRetryPolicy` wraps calls inside `SqlJobStorage`.

Storage options:

| Setting | Purpose |
|---|---|
| `MaxTransientRetries` | Maximum retry attempts for transient SQL exceptions. |
| `TransientRetryBaseDelayMs` | Base delay for exponential backoff. |
| `CommandTimeoutSeconds` | Timeout for individual SQL commands. |

## Transient Errors

The policy treats these as retryable:

- `SqlException.IsTransient`;
- `1205` deadlock;
- `-2` timeout;
- `4060` cannot open database;
- `40197` Azure SQL request processing error;
- `40501` service busy;
- `40613` database unavailable.

## Backoff And Jitter

Delay grows exponentially:

```text
baseDelayMs * 2^(attempt - 1)
```

Jitter adds random spread so multiple workers do not retry in lockstep after a
shared SQL blip.

## Difference From Job Retry

| Retry type | Retries what? | Visible as job attempt? |
|---|---|---|
| SQL transient retry | ChokaQ storage operation. | No |
| Job retry | User handler execution. | Yes |

A transient SQL retry should not burn a job attempt unless the job actually
reaches user-code execution.

## Architecture Decision

### Why this pattern?

ChokaQ needs a narrow SQL retry shape and avoids taking a general-purpose
resilience dependency for one storage concern.

### Trade-offs

A local policy is smaller and explicit, but less feature-rich than libraries
like Polly. If future provider behavior becomes more complex, this may deserve
a pluggable policy.

### Alternatives considered

| Alternative | Benefit | Cost |
|---|---|---|
| No SQL retry | Simple failure visibility. | Too brittle under deadlocks/network blips. |
| Polly dependency | Mature resilience features. | Extra dependency for one narrow path. |
| Retry every SQL error | More aggressive recovery. | Can hide real bugs and overload SQL. |

### Interview questions

**Why not count SQL retry as job retry?**  
Because user code did not run. Job attempts track handler execution, not storage
plumbing retries.

**Why add jitter?**  
To avoid workers retrying at the same time after a shared outage.

**What errors should not be retried?**  
Schema errors, invalid SQL, permission failures, and data contract violations.

