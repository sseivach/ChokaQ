# Middleware Pipeline

![Middleware pipeline](/diagrams/43-middleware-pipeline.png)

ChokaQ middleware wraps handler execution with cross-cutting behavior. It is
similar in spirit to ASP.NET Core middleware, but scoped to job execution.

Middleware is not responsible for fetching, SQL leases, retry scheduling,
circuit breaker decisions, Archive, or DLQ. Those remain runtime lifecycle
policy.

## Where It Lives

The public contract is `IChokaQMiddleware`. Dispatchers build the pipeline:

- `BusJobDispatcher` passes the deserialized job DTO;
- `PipeJobDispatcher` passes the raw payload string.

Middleware runs around the core handler delegate.

## Execution Order

Middleware is registered in application configuration and wrapped around the
handler in order. A middleware can:

- log;
- add correlation;
- audit;
- validate;
- time execution;
- enforce idempotency;
- stop before calling `next()`.

## What Middleware Should Not Do

Middleware should not:

- mutate ChokaQ storage state directly;
- move jobs to Archive or DLQ;
- bypass the retry policy;
- swallow cancellation silently;
- perform long blocking work before the handler;
- hide failures that should be visible to the processor.

If middleware throws, the processor treats it as execution failure and applies
normal retry/DLQ policy.

## Architecture Decision

### Why this pattern?

Middleware gives applications one consistent hook for cross-cutting execution
concerns without forcing every handler to repeat the same boilerplate.

### Trade-offs

Middleware adds ordering complexity. A logging middleware, idempotency
middleware, and validation middleware can behave differently depending on order,
so registration should be explicit and documented.

### Alternatives considered

| Alternative | Benefit | Cost |
|---|---|---|
| Handler base class | Simple inheritance. | Blocks composition and multiple concerns. |
| Attributes only | Declarative. | Harder dependency injection and ordering. |
| No middleware | Smaller runtime. | Repeated handler boilerplate. |

### Interview questions

**Why not let middleware own final state transitions?**  
Because lifecycle correctness must stay centralized in the processor/storage
boundary.

**What happens if middleware fails?**  
The job execution fails like a handler failure and goes through retry/DLQ policy.

**Why pass raw payload in Pipe mode?**  
Pipe mode deliberately exposes the raw event stream shape to one global handler.

