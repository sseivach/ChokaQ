# SignalR Notification Contract

![SignalR notification contract](/diagrams/53-signalr-notification-contract.png)

The Deck использует SignalR как real-time notification layer. SignalR - не source of truth; source of truth - SQL storage. SignalR сообщает connected dashboards, что что-то изменилось и какие UI areas нужно refresh или update.

## Где это находится

| Runtime type | Role |
|---|---|
| `IChokaQNotifier` | Runtime notification contract. |
| `ChokaQSignalRNotifier` | SignalR implementation, отправляющая events clients. |
| `ChokaQHub` | Bidirectional hub для dashboard commands и live updates. |

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

## Correctness boundary

SignalR messages могут задерживаться, теряться при disconnects или наблюдаться browser'ом out of order. Это допустимо, потому что dashboard всегда может reload authoritative state from storage.

Event contract нужно рассматривать как UI invalidation protocol, а не durable event stream.

## Архитектурное решение

### Почему этот pattern?

Operators нужен low-latency feedback, когда jobs move, fail, resurrect или purge. SignalR подходит для interactive dashboard updates без добавления отдельного broker.

### Trade-offs

SignalR требует connected clients и authorization на hub. Он не заменяет storage reads или metrics. Dashboard code должен tolerate reconnects и refresh from storage.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| Polling only | Просто и robust. | Медленнее operator feedback и больше periodic reads. |
| External pub/sub | Возможен durable fanout. | Extra infrastructure для dashboard invalidation. |
| Browser refresh only | Minimal code. | Poor incident workflow. |

### Дополнительные вопросы

**SignalR authoritative?**  
Нет. SQL authoritative. SignalR - notification и invalidation layer.

**Что если client пропустит event?**  
Следующий refresh или explicit storage query вернет authoritative state.

**Почему не broker?**  
Для dashboard invalidation SignalR достаточно, и он не добавляет infrastructure.
