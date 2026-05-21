# Job Context And Cancellation

![Job context and cancellation](/diagrams/45-job-context-cancellation.png)

`IJobContext` gives handlers access to execution context without exposing worker
internals.

Handlers can:

- read the current `JobId`;
- observe the current execution `CancellationToken`;
- report progress to The Deck.

## Context Surface

```csharp
public interface IJobContext
{
    string JobId { get; }
    CancellationToken CancellationToken { get; }
    Task ReportProgressAsync(int percentage);
}
```

## Cancellation Sources

The execution token can be cancelled by:

| Source | Meaning |
|---|---|
| Handler timeout | Execution exceeded configured runtime budget. |
| Admin cancellation | Operator requested stop from The Deck or worker API. |
| Worker shutdown | Host is stopping and execution should finish or cancel. |

Cancellation is cooperative. Handlers must pass the token to downstream async
calls and check it in long-running loops.

## Progress Reporting

`ReportProgressAsync` clamps progress to `0..100` and notifies The Deck through
the runtime notifier. Progress is a UI signal, not a state transition.

## Developer Guidance

Good handlers:

- pass `CancellationToken` to database, HTTP, storage, and SDK calls;
- avoid swallowing `OperationCanceledException`;
- report progress only at meaningful boundaries;
- keep external side effects idempotent;
- do not assume cancellation means rollback happened downstream.

## Architecture Decision

### Why this pattern?

The handler needs execution context, but it should not know about workers,
SignalR, SQL leases, or cancellation registries. `IJobContext` keeps the public
surface small.

### Trade-offs

The context is intentionally limited. It does not expose mutable job state or
storage APIs because that would let handlers bypass lifecycle policy.

### Alternatives considered

| Alternative | Benefit | Cost |
|---|---|---|
| Pass worker object to handlers | Maximum power. | Breaks encapsulation and lifecycle safety. |
| Ambient static context | Convenient. | Harder tests and hidden coupling. |
| No context | Simple. | No progress reporting or job-local cancellation surface. |

### Additional Questions

**Is cancellation guaranteed to stop side effects?**  
No. It is cooperative. Downstream systems may already have accepted work.

**Why not expose storage from context?**  
Because handlers should not perform lifecycle transitions directly.

**What should a handler do with the token?**  
Pass it to all cancellable async operations and stop cleanly when requested.

