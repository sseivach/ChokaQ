# SignalR Notification Contract

![SignalR notification contract](/diagrams/53-signalr-notification-contract.png)

The Deck uses SignalR as a real-time notification layer. SignalR is not the
source of truth; SQL storage is. SignalR tells connected dashboards that
something changed and which UI areas should refresh or update.

## Where It Lives

| Runtime type | Role |
|---|---|
| `IChokaQNotifier` | Runtime notification contract. |
| `ChokaQSignalRNotifier` | SignalR implementation that sends events to clients. |
| `ChokaQHub` | Bidirectional hub for dashboard commands and live updates. |

## Events

| Event | Meaning |
|---|---|
| `JobUpdated` | Active job status changed. |
| `JobProgress` | Handler reported progress. |
| `JobArchived` | Job moved to successful history. |
| `JobFailed` | Job moved to DLQ. |
| `JobResurrected` | DLQ job moved back to active work. |
| `JobsPurged` | Jobs were permanently deleted. |
| `QueueStateChanged` | Queue pause/resume changed. |
| `StatsUpdated` | Dashboard counters should refresh. |

## Correctness Boundary

SignalR messages can be delayed, dropped by disconnects, or observed out of
order by a browser. That is acceptable because the dashboard can always reload
authoritative state from storage.

The event contract should be treated as a UI invalidation protocol, not as a
durable event stream.

## Architecture Decision

### Why this pattern?

Operators need low-latency feedback when jobs move, fail, resurrect, or purge.
SignalR fits interactive dashboard updates without adding a separate broker.

### Trade-offs

SignalR requires connected clients and authorization on the hub. It does not
replace storage reads or metrics. Dashboard code must tolerate reconnects and
refresh from storage.

### Alternatives considered

| Alternative | Benefit | Cost |
|---|---|---|
| Polling only | Simple and robust. | Slower operator feedback and more periodic reads. |
| External pub/sub | Durable fanout possible. | Extra infrastructure for dashboard invalidation. |
| Browser refresh only | Minimal code. | Poor incident workflow. |

### Interview questions

**Is SignalR authoritative?**  
No. SQL is authoritative. SignalR is a notification and invalidation layer.

**What happens if a client misses an event?**  
The next refresh or explicit storage query returns authoritative state.

**Why not use a broker?**  
For dashboard invalidation, SignalR is enough and avoids adding infrastructure.

