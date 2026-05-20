# Bus Vs Pipe Dispatch

![Bus vs Pipe dispatch](/diagrams/44-bus-vs-pipe-dispatch.png)

ChokaQ has two dispatch modes.

| Mode | Best for | Handler shape |
|---|---|---|
| Bus | Typed domain jobs. | `IChokaQJobHandler<TJob>` |
| Pipe | Raw event-style payloads. | `IChokaQPipeHandler` |

Bus mode is the default choice for application workflows. Pipe mode is a lower
level path when a host wants one handler to receive many job types as raw
payloads.

## Bus Mode

Bus mode flow:

1. Persisted type key resolves to a CLR job type.
2. Payload deserializes into the job DTO.
3. DI resolves `IChokaQJobHandler<TJob>`.
4. A compiled expression delegate invokes `HandleAsync`.
5. Middleware wraps the handler.

Bus mode gives strong typing, normal DI, and compile-time handler contracts.

## Pipe Mode

Pipe mode flow:

1. The worker reads the persisted job type string.
2. Payload remains raw.
3. DI resolves one `IChokaQPipeHandler`.
4. Middleware wraps the pipe handler.
5. The handler receives `(jobType, payload, cancellationToken)`.

Pipe mode fits high-throughput routing, integration events, or hosts that own
their own payload parsing.

## Shared Runtime Policy

Both modes share:

- durable storage;
- worker fetch;
- circuit breaker check;
- retry/DLQ policy;
- heartbeat;
- metrics;
- The Deck visibility;
- middleware pipeline.

Only dispatch shape changes.

## Architecture Decision

### Why this pattern?

The product needs ergonomic typed handlers for normal users and a raw dispatch
path for advanced/high-throughput scenarios. Keeping both modes behind one
processor lets reliability policy stay consistent.

### Trade-offs

Two modes increase documentation and testing surface. The benefit is that
ChokaQ can serve both domain-command workflows and raw event processing without
forking the runtime.

### Alternatives considered

| Alternative | Benefit | Cost |
|---|---|---|
| Bus only | Simpler mental model. | Less flexible for raw event streams. |
| Pipe only | Minimal dispatch model. | Loses typed handler ergonomics. |
| Separate products | Strong isolation. | Duplicated runtime policy. |

### Interview questions

**Which mode should most apps start with?**  
Bus mode, because typed contracts and handlers are easier to reason about.

**Why keep Pipe mode?**  
It supports raw payload processing without forcing every event into a typed
handler registration.

**Do reliability guarantees differ by mode?**  
No. Storage, retries, DLQ, heartbeat, and circuit breakers are shared.

