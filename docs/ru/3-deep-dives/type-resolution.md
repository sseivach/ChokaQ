# Type Resolution

![Type resolution registry](/diagrams/41-type-resolution-registry.png)

Type resolution - это механизм, с помощью которого ChokaQ мапит persisted string key из SQL на .NET job type во время runtime.

Persisted key - durable contract. CLR type - implementation detail, который можно rename, move или version.

## Где это находится

`JobTypeRegistry` поддерживает два mapping:

| Direction | Use |
|---|---|
| key to type | Worker dispatch при чтении persisted jobs. |
| type to key | Enqueue path при serialization нового job. |

Profiles заполняют registry при startup.

## Recommended keys

Используйте semantic versioned keys:

```csharp
CreateJob<SendEmailJob, EmailHandler>("email.send.v1");
CreateJob<CapturePaymentJob, CapturePaymentHandler>("billing.capture-payment.v1");
```

Не persist'ите короткие CLR names вроде `SendEmailJob`. Они удобны, пока не изменится namespace, assembly или class name.

## Strict mode

`ChokaQ:TypeResolution:RequireRegisteredJobTypes` управляет тем, разрешены ли unregistered job types.

Strict registration безопаснее для production:

- startup profiles определяют contract surface;
- unknown SQL rows fail clearly;
- refactors не меняют persistence keys silently.

Compatibility fallback может использовать assembly-qualified names, но это связывает stored jobs с CLR identity.

## Failure modes

| Failure | Cause | Fix |
|---|---|---|
| Unknown type key | Profile missing или wrong key. | Register key или migrate row. |
| Duplicate key | Два profiles claim'ят один key. | Сделать keys globally unique. |
| Old row after refactor | CLR fallback key changed. | Использовать semantic keys и migration strategy. |
| Payload mismatch | Type resolved, но payload contract changed. | Version type keys и payload DTOs. |

## Архитектурное решение

### Почему этот pattern?

Durable jobs живут дольше code deployments. Stable string contract безопаснее, чем persisting raw CLR identity как primary dispatch mechanism.

### Trade-offs

Semantic keys требуют discipline. Developers должны version contracts и держать old handlers доступными во время migration windows.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| Persist CLR type name | Легко на старте. | Ломается при refactor и assembly changes. |
| Store only numeric type IDs | Compact. | Требует central registry и сложнее debugging. |
| Dynamic assembly scanning | Flexible. | Slow, unsafe и unpredictable в trimmed/AOT hosts. |

### Дополнительные вопросы

**Почему не persist CLR type names?**  
Потому что SQL rows могут пережить refactors. Persisted contracts должны быть stable across code movement.

**Как rollout'ить `v2` payloads?**  
Register new type key, держать `v1` handler support, пока old rows не drain'ятся, затем retire old key через explicit migration/retention plan.

**Что должно происходить с unknown type key?**  
Он должен fail visibly и быть operator-diagnosable, а не silently dispatch'иться в unsafe fallback.
