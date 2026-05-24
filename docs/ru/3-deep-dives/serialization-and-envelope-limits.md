# Serialization And Envelope Limits

![Serialization and envelope limits](/diagrams/42-serialization-envelope-limits.png)

Serialization превращает typed job object в durable payload text. Envelope validation защищает storage contract вокруг этого payload: queue name, type key, creator, tags, idempotency key и payload size.

Payload - это business data. Envelope - routing и operational metadata ChokaQ.

## Где это находится

| Area | Runtime type |
|---|---|
| JSON serializer | `SystemTextJsonChokaQJobSerializer` |
| Serializer options | `ChokaQSerializationOptions` |
| Envelope validation | `ChokaQEnvelopeLimits` |
| Type key lookup | `JobTypeRegistry` |

## Payload serialization

Bus mode serializes job DTO через `System.Text.Json` с configured serializer options. Во время dispatch persisted type key resolves в CLR type, а payload deserializes в этот type.

Pipe mode сохраняет payload как raw JSON/text и передает его одному global pipe handler.

## Envelope limits

ChokaQ validates:

| Field | Limit |
|---|---|
| Queue | 255 characters |
| Type key | 255 characters |
| CreatedBy | 100 characters |
| Tags | 1000 characters |
| Idempotency key | 255 characters |
| Payload | `Serialization.MaxPayloadBytes` |

Эти limits соответствуют storage reality и предотвращают accidental unbounded operational metadata.

## Contract versioning

Persisted payloads могут жить дольше deployments. Относитесь к job DTOs как к message contracts:

- предпочитайте additive changes;
- не rename'ьте required fields без compatibility plan;
- используйте semantic type keys вроде `email.send.v1`;
- register `v2` side by side при breaking payload shape;
- держите old handlers, пока old rows не drain'ятся или не migrated.

## Архитектурное решение

### Почему этот pattern?

ChokaQ использует простой JSON payload contract, потому что он inspectable в SQL и The Deck, его удобно version'ить, и он естественен для .NET applications.

### Trade-offs

JSON больше binary formats и schema compatibility остается developer discipline. Выигрыш - operational readability и меньшая dependency burden.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| Binary serialization | Меньше/быстрее. | Сложнее inspection и version debugging. |
| Persist CLR object graph | Convenient. | Fragile across deployments. |
| External schema registry | Strong governance. | Больше infrastructure и friction для preview scope. |

### Дополнительные вопросы

**Почему validate envelope length before storage?**  
Потому что database columns и indexes имеют конечные limits. Fail early дает clear errors вместо SQL truncation или index failures.

**Почему semantic type keys?**  
Потому что serialized rows живут дольше CLR refactors.

**Какая breaking-change strategy?**  
Register new type key, держать old handlers во время migration, затем retire old contracts после retention или explicit migration.
