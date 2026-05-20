# Type Resolution

![Type resolution registry](/diagrams/41-type-resolution-registry.png)

Type resolution is how ChokaQ maps a persisted string key in SQL to a .NET job
type at runtime.

The persisted key is the durable contract. The CLR type is an implementation
detail that can be renamed, moved, or versioned.

## Where It Lives

`JobTypeRegistry` maintains two mappings:

| Direction | Use |
|---|---|
| key to type | Worker dispatch when reading persisted jobs. |
| type to key | Enqueue path when serializing a new job. |

Profiles populate the registry at startup.

## Recommended Keys

Use semantic versioned keys:

```csharp
CreateJob<SendEmailJob, EmailHandler>("email.send.v1");
CreateJob<CapturePaymentJob, CapturePaymentHandler>("billing.capture-payment.v1");
```

Avoid persisting short CLR names such as `SendEmailJob`. They are convenient
until a namespace, assembly, or class name changes.

## Strict Mode

`ChokaQ:TypeResolution:RequireRegisteredJobTypes` controls whether unregistered
job types are allowed.

Strict registration is safer for production:

- startup profiles define the contract surface;
- unknown SQL rows fail clearly;
- refactors do not silently change persistence keys.

Compatibility fallback can use assembly-qualified names, but that couples
stored jobs to CLR identity.

## Failure Modes

| Failure | Cause | Fix |
|---|---|---|
| Unknown type key | Profile missing or wrong key. | Register the key or migrate the row. |
| Duplicate key | Two profiles claim same key. | Make keys globally unique. |
| Old row after refactor | CLR fallback key changed. | Use semantic keys and migration strategy. |
| Payload mismatch | Type resolved but payload contract changed. | Version type keys and payload DTOs. |

## Architecture Decision

### Why this pattern?

Durable jobs outlive code deployments. A stable string contract is safer than
persisting raw CLR identity as the primary dispatch mechanism.

### Trade-offs

Semantic keys require discipline. Developers must version contracts and keep old
handlers available during migration windows.

### Alternatives considered

| Alternative | Benefit | Cost |
|---|---|---|
| Persist CLR type name | Easy at first. | Breaks on refactor and assembly changes. |
| Store only numeric type IDs | Compact. | Requires central registry and harder debugging. |
| Dynamic assembly scanning | Flexible. | Slow, unsafe, and unpredictable in trimmed/AOT hosts. |

### Interview questions

**Why not persist CLR type names?**  
Because SQL rows can outlive refactors. Persisted contracts should be stable
across code movement.

**How do you roll out `v2` payloads?**  
Register a new type key, keep `v1` handler support until old rows drain, then
retire the old key through an explicit migration/retention plan.

**What should happen to an unknown type key?**  
It should fail visibly and be operator-diagnosable, not dispatch to an unsafe
fallback silently.

