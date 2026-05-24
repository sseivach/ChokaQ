# System.Threading.Channels

![System.Threading.Channels flow](/diagrams/27-system-threading-channels-flow.png)

ChokaQ использует `System.Threading.Channels` как in-process coordination primitive. Channels - не durable queue. Это local buffers, которые соединяют producer loops и consumer loops без blocking всего worker.

## Где используются channels

| Area | Channel role |
|---|---|
| SQL worker | Local bounded prefetch buffer между SQL fetch и execution. |
| In-memory queue | Primary local queue для development и tests. |

В SQL Server mode SQL остается durable source of truth. В in-memory mode channel является queue, и jobs теряются при process exit.

## Почему channels подходят

Channels дают:

- async producer/consumer flow;
- bounded capacity;
- backpressure через `BoundedChannelFullMode.Wait`;
- low allocation overhead по сравнению с ad hoc locks;
- clean shutdown через writer completion и cancellation.

## Correctness boundary

Channels не предоставляют durability, distributed coordination или cross-process visibility. ChokaQ полагается на channels только для local buffering. Durable state приходит из SQL rows и SQL transitions.

## Архитектурное решение

### Почему этот pattern?

Channels - standard .NET primitive для async producer/consumer pipelines. Они держат local worker code простым и позволяют не писать custom queue implementations.

### Trade-offs

Channels process-local. Все, что лежит только в channel, исчезает при process exit. Это допустимо для SQL prefetch, потому что SQL owns source row, и recovery может reset'ить stale fetched rows. Но этого недостаточно для production durable queue само по себе.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| `BlockingCollection` | Familiar. | Старый blocking model, слабее async ergonomics. |
| Custom lock queue | Full control. | Легко ошибиться в cancellation/backpressure. |
| External broker | Durable и scalable. | Extra infrastructure и другой product scope. |

### Дополнительные вопросы

**Channels - это queue?**  
Только в in-memory mode. В SQL Server mode channels - local buffers.

**Почему bounded channels?**  
Потому что unbounded local buffering скрывает pressure и рискует memory exhaustion.

**Что происходит на shutdown?**  
Writer completes, processors drain/release work, а SQL recovery обрабатывает stale fetched rows, если process exited до clean release.
