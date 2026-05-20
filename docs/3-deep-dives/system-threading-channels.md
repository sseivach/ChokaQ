# System.Threading.Channels

![System.Threading.Channels flow](/diagrams/27-system-threading-channels-flow.png)

ChokaQ uses `System.Threading.Channels` as an in-process coordination primitive.
Channels are not the durable queue. They are local buffers used to connect
producer loops and consumer loops without blocking the whole worker.

## Where Channels Appear

| Area | Channel role |
|---|---|
| SQL worker | Local bounded prefetch buffer between SQL fetch and execution. |
| In-memory queue | Primary local queue for development and tests. |

In SQL Server mode, SQL remains the durable source of truth. In in-memory mode,
the channel is the queue and jobs are lost when the process exits.

## Why Channels Fit

Channels provide:

- async producer/consumer flow;
- bounded capacity;
- backpressure via `BoundedChannelFullMode.Wait`;
- low allocation overhead compared with ad hoc locks;
- clean shutdown through writer completion and cancellation.

## Correctness Boundary

Channels do not provide durability, distributed coordination, or cross-process
visibility. ChokaQ only relies on channels for local buffering. Durable state
comes from SQL rows and SQL transitions.

## Architecture Decision

### Why this pattern?

Channels are a standard .NET primitive for async producer/consumer pipelines.
They keep local worker code simple while avoiding custom queue implementations.

### Trade-offs

Channels are process-local. Anything placed only in a channel disappears when
the process exits. That is acceptable for SQL prefetch because SQL owns the
source row and recovery can reset stale fetched rows. It is not acceptable as a
production durable queue by itself.

### Alternatives considered

| Alternative | Benefit | Cost |
|---|---|---|
| `BlockingCollection` | Familiar. | Older blocking model, weaker async ergonomics. |
| Custom lock queue | Full control. | Easy to get cancellation/backpressure wrong. |
| External broker | Durable and scalable. | Extra infrastructure and different product scope. |

### Interview questions

**Are channels the queue?**  
Only in in-memory mode. In SQL Server mode, channels are local buffers.

**Why bounded channels?**  
Because unbounded local buffering hides pressure and risks memory exhaustion.

**What happens on shutdown?**  
The writer completes, processors drain/release work, and SQL recovery handles
stale fetched rows if the process exits before clean release.

