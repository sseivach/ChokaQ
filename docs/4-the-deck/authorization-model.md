# Authorization Model

![The Deck authorization model](/diagrams/54-thedeck-authorization-model.png)

The Deck is an administrative control plane. It can inspect jobs, cancel work,
edit payloads, requeue DLQ rows, purge data, pause queues, and change queue
runtime controls. It is secure by default.

## Options

| Option | Meaning |
|---|---|
| `AuthorizationPolicy` | Read/connect policy for dashboard and hub. |
| `DestructiveAuthorizationPolicy` | Stronger policy for write/destructive hub commands. |
| `AllowAnonymousDeck` | Explicit demo/sandbox escape hatch. Default is `false`. |

If `AuthorizationPolicy` is not set, The Deck uses the host application's
default authorization boundary. If `AllowAnonymousDeck` is true, policies cannot
also be configured.

## Endpoint Protection

`MapChokaQTheDeck()` maps:

- static assets;
- SignalR hub at `{RoutePrefix}/hub`;
- dashboard Razor components at `{RoutePrefix}`.

Unless anonymous access is explicitly enabled, both hub and dashboard require
authorization.

## Destructive Policy

The hub checks `DestructiveAuthorizationPolicy` at method level for operations
that mutate jobs or queues. This enables read-only operators to inspect the
system without granting purge/edit/requeue privileges.

## Architecture Decision

### Why this pattern?

Dashboard read access and destructive command access are different privileges.
Production systems often need more people to observe than to mutate.

### Trade-offs

Two policies add configuration burden. The payoff is a clean separation between
incident visibility and destructive authority.

### Alternatives considered

| Alternative | Benefit | Cost |
|---|---|---|
| Anonymous by default | Easy demos. | Unsafe for production. |
| One policy for everything | Simple. | No read-only operator role. |
| App-specific auth inside every component | Flexible. | Repeated and error-prone. |

### Additional Questions

**Why secure The Deck by default?**  
Because it is a write-capable operations console.

**Why have a separate destructive policy?**  
Because inspect and mutate are different operational privileges.

**When is anonymous access acceptable?**  
Only for local demos or intentionally public sandboxes.

