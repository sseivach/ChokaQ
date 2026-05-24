# Authorization Model

![The Deck authorization model](/diagrams/54-thedeck-authorization-model.png)

The Deck - administrative control plane. Он может inspect jobs, cancel work, edit payloads, requeue DLQ rows, purge data, pause queues и менять queue runtime controls. Он secure by default.

## Options

| Option | Meaning |
|---|---|
| `AuthorizationPolicy` | Read/connect policy для dashboard и hub. |
| `DestructiveAuthorizationPolicy` | Более строгая policy для write/destructive hub commands. |
| `AllowAnonymousDeck` | Explicit demo/sandbox escape hatch. Default is `false`. |

Если `AuthorizationPolicy` не задан, The Deck использует default authorization boundary host application. Если `AllowAnonymousDeck` true, policies нельзя одновременно configure'ить.

## Endpoint protection

`MapChokaQTheDeck()` maps:

- static assets;
- SignalR hub at `{RoutePrefix}/hub`;
- dashboard Razor components at `{RoutePrefix}`.

Если anonymous access не включен явно, hub и dashboard требуют authorization.

## Destructive policy

Hub проверяет `DestructiveAuthorizationPolicy` на уровне methods для operations, которые mutate jobs или queues. Это позволяет read-only operators inspect'ить system без purge/edit/requeue privileges.

## Архитектурное решение

### Почему этот pattern?

Dashboard read access и destructive command access - разные privileges. Production systems часто требуют, чтобы больше людей могло observe, чем mutate.

### Trade-offs

Две policies добавляют configuration burden. Выигрыш - чистое разделение incident visibility и destructive authority.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| Anonymous by default | Easy demos. | Unsafe for production. |
| One policy for everything | Просто. | Нет read-only operator role. |
| App-specific auth inside every component | Flexible. | Repeated и error-prone. |

### Дополнительные вопросы

**Почему The Deck secure by default?**  
Потому что это write-capable operations console.

**Зачем отдельная destructive policy?**  
Потому что inspect и mutate - разные operational privileges.

**Когда anonymous access приемлем?**  
Только для local demos или intentionally public sandboxes.
