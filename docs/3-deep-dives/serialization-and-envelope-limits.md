# Serialization And Envelope Limits

![Serialization and envelope limits](/diagrams/42-serialization-envelope-limits.png)

Serialization turns a typed job object into durable payload text. Envelope
validation protects the storage contract around that payload: queue name, type
key, creator, tags, idempotency key, and payload size.

The payload is business data. The envelope is ChokaQ routing and operational
metadata.

## Where It Lives

| Area | Runtime type |
|---|---|
| JSON serializer | `SystemTextJsonChokaQJobSerializer` |
| Serializer options | `ChokaQSerializationOptions` |
| Envelope validation | `ChokaQEnvelopeLimits` |
| Type key lookup | `JobTypeRegistry` |

## Payload Serialization

Bus mode serializes the job DTO with `System.Text.Json` using configured
serializer options. During dispatch, the persisted type key resolves to a CLR
type and the payload is deserialized into that type.

Pipe mode keeps the payload as raw JSON/text and passes it to one global pipe
handler.

## Envelope Limits

ChokaQ validates:

| Field | Limit |
|---|---|
| Queue | 255 characters |
| Type key | 255 characters |
| CreatedBy | 100 characters |
| Tags | 1000 characters |
| Idempotency key | 255 characters |
| Payload | `Serialization.MaxPayloadBytes` |

These limits match storage reality and prevent accidental unbounded operational
metadata.

## Contract Versioning

Persisted payloads can outlive deployments. Treat job DTOs like message
contracts:

- prefer additive changes;
- avoid renaming required fields without a compatibility plan;
- use semantic type keys such as `email.send.v1`;
- register `v2` side by side when breaking payload shape;
- keep old handlers until old rows drain or are migrated.

## Architecture Decision

### Why this pattern?

ChokaQ uses a simple JSON payload contract because it is inspectable in SQL and
The Deck, easy to version, and natural for .NET applications.

### Trade-offs

JSON is larger than binary formats and schema compatibility is a developer
discipline. The benefit is operational readability and lower dependency burden.

### Alternatives considered

| Alternative | Benefit | Cost |
|---|---|---|
| Binary serialization | Smaller/faster. | Harder inspection and version debugging. |
| Persist CLR object graph | Convenient. | Fragile across deployments. |
| External schema registry | Strong governance. | More infrastructure and friction for preview scope. |

### Additional Questions

**Why validate envelope length before storage?**  
Because database columns and indexes have finite limits. Failing early produces
clear errors instead of SQL truncation or index failures.

**Why use semantic type keys?**  
Because serialized rows outlive CLR refactors.

**What is the breaking-change strategy?**  
Register a new type key, keep old handlers during migration, then retire old
contracts after retention or explicit migration.

