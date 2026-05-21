# Queue Registry And Job Profiles

![Queue registry and profiles](/diagrams/46-queue-registry-profiles.png)

Profiles are the compile-time registration surface for typed jobs. Queues are
the runtime partition and operational control surface.

They solve different problems:

| Concept | Purpose |
|---|---|
| Job profile | Maps persisted type keys to DTOs and handlers. |
| Queue | Groups runtime work for throttling, pause, lag, and worker budgets. |

## Job Profiles

A profile registers job contracts:

```csharp
public sealed class BillingProfile : ChokaQJobProfile
{
    public BillingProfile()
    {
        CreateJob<CapturePaymentJob, CapturePaymentHandler>("billing.capture-payment.v1");
    }
}
```

The profile fills the type registry at startup. Duplicate type keys fail fast.

## Queues

Queues are stored in SQL and can be created automatically when jobs are
enqueued. The `Queues` table stores runtime configuration:

- `IsPaused`;
- `IsActive`;
- `ZombieTimeoutSeconds`;
- `MaxWorkers`;
- `LastUpdatedUtc`.

Workers read queue configuration during fetch and processing.

## Relationship

A job type can run in different queues depending on how it is enqueued. The
type key controls dispatch. The queue controls operational behavior.

Example:

- `email.send.v1` in `transactional-email`;
- `email.send.v1` in `marketing-email`;
- same handler contract;
- different queue lag, pause, and worker caps.

## Architecture Decision

### Why this pattern?

Dispatch identity and operational partitioning are separate concerns. Combining
them would make every queue change a type-contract change.

### Trade-offs

Operators must understand that queue names do not equal job types. Documentation
and The Deck must make both visible.

### Alternatives considered

| Alternative | Benefit | Cost |
|---|---|---|
| One queue per type key | Simple mapping. | Too rigid for operational grouping. |
| Queue owns handler mapping | Operationally direct. | Makes dispatch depend on runtime queue config. |
| No named queues | Simpler storage. | No per-queue throttling or pause. |

### Additional Questions

**Why separate type key from queue?**  
Type key is the message contract. Queue is an operational partition.

**Can one job type appear in multiple queues?**  
Yes, if the application enqueues it that way.

**What should be globally unique?**  
Type keys in profiles. Queue names are operational labels.

