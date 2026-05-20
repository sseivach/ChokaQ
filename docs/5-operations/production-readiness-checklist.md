# Production Readiness Checklist

![Production readiness map](/diagrams/08-production-readiness-map.png)

Use this checklist before enabling ChokaQ for production work.

## Package And Runtime

- Install the top-level `ChokaQ` package unless you are intentionally testing
  internal project references.
- Pin a preview version explicitly.
- Verify the app starts with the same package graph that will be deployed.
- Run the NuGetLab sample against a fresh database before release.

## SQL Server

- Use a dedicated database or schema.
- Confirm the app identity has the right bootstrap permissions if
  `AutoCreateSqlTable = true`.
- If bootstrap permissions are not allowed in production, run schema creation
  through a deployment migration step.
- Set `CommandTimeoutSeconds` to a value that fails fast enough for workers and
  dashboard operations.
- Size connection pools for workers, dashboard reads, health checks, and admin
  operations.
- Monitor database CPU, waits, lock waits, deadlocks, and log growth.

## Storage Retention

- Define Archive retention.
- Define DLQ retention.
- Decide whether purging requires approval.
- Keep enough DLQ history for incident investigation.
- Do not let audit retention become accidental admission control.

## Worker Policy

- Set retry limits based on downstream behavior.
- Add jitter to avoid synchronized retry storms.
- Tune `ProcessingZombieTimeout` above normal handler duration and heartbeat
  jitter.
- Configure queue-specific timeouts for long-running queues.
- Use max worker caps for downstream-limited queues.

## Handler Safety

- Treat execution as at-least-once.
- Use business idempotency keys for external side effects.
- Use provider idempotency for payments and webhooks.
- Use unique constraints or upserts for database writes.
- Avoid non-versioned payload contracts.
- Do not depend on CLR type names as persisted contracts.

## The Deck

- Require authentication.
- Use a separate destructive authorization policy for purge/requeue/cancel when
  possible.
- Disable anonymous access outside demos.
- Verify `/chokaq` loads static assets from the packaged application.
- Document who is allowed to operate DLQ and queue controls.

The detailed dashboard security model is documented in [Authorization Model](/4-the-deck/authorization-model) and [Destructive Actions](/4-the-deck/destructive-actions).

## Health Checks

- Expose health checks to infrastructure, not the public internet.
- Include SQL connectivity.
- Include worker heartbeat.
- Include queue lag/saturation.
- Alert on stale worker heartbeat, high lag, and growing DLQ.

Detailed probe semantics are documented in [Health Checks](/5-operations/health-checks).

## Metrics And Logs

- Export the `ChokaQ` meter.
- Cap metric tag cardinality.
- Alert on queue lag, failure rate, DLQ growth, heartbeat failures, and open
  circuits.
- Keep structured logs for worker lifecycle, retry scheduling, DLQ moves,
  resurrection, purge, and circuit events.

## Deployment Smoke Test

Before declaring the release healthy:

1. Start with a fresh database.
2. Confirm schema bootstrap or migration completes without first-start SQL errors.
3. Enqueue a successful job.
4. Confirm it appears in `JobsHot`.
5. Confirm it moves to `JobsArchive`.
6. Enqueue a failing job.
7. Confirm retry behavior.
8. Confirm DLQ move.
9. Open The Deck.
10. Confirm `/health` reports healthy or expected degraded status.

## Architecture Decision

### Why this checklist?

Most production failures are not caused by one missing API call. They happen at
the boundary between code, database, deployment, observability, and operator
permissions. This checklist forces those boundaries into the release process.

### Interview questions

**What is the first production metric you would watch?**  
Queue lag. Depth matters, but lag tells you how long eligible work is waiting.

**What is the riskiest operator action?**  
Bulk purge, because it destroys recovery evidence.

**What is the riskiest handler bug?**  
A non-idempotent external side effect combined with retry or zombie
resurrection.
