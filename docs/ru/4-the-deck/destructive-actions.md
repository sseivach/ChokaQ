# Destructive Actions

![Destructive actions safety](/diagrams/55-destructive-actions-safety.png)

Destructive actions - operations, которые mutate job state, queue state или stored evidence. Им нужны более строгие safety rules, чем обычным dashboard reads.

## Destructive commands

Примеры:

- cancel job;
- restart job;
- resurrect DLQ job;
- repair and requeue DLQ job;
- edit payload/tags/priority;
- purge DLQ rows;
- bulk cancel;
- bulk restart;
- bulk requeue by filter;
- bulk purge by filter;
- pause/resume queue;
- change queue timeout;
- change max workers.

## Safety layers

| Layer | Purpose |
|---|---|
| Hub input validation | Reject malformed IDs, queue names, payloads, tags, priorities и filters. |
| Destructive authorization policy | Require stronger privilege for mutation. |
| Batch size caps | Prevent huge accidental operations. |
| JSON validation | Prevent edited payloads from becoming poison pills. |
| Filter normalization | Keep custom SignalR clients within supported bounds. |
| Typed confirmation in UI | Force operator intent before high-impact actions. |
| Logs with actor | Preserve audit evidence. |

## Bulk operations

Bulk operations всегда нужно считать incident-grade actions. Safe flow:

1. Filter narrowly.
2. Preview count and scope.
3. Confirm typed token.
4. Execute bounded batch.
5. Refresh history page and stats.
6. Watch failure rate and queue lag.

## Архитектурное решение

### Почему этот pattern?

The Deck нужна repair power во время incidents, но repair power может destroy data или amplify failure. Safety должна существовать и на UI boundary, и на hub boundary.

### Trade-offs

Extra confirmation замедляет expert operators. Для irreversible actions вроде purge это намеренно.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| UI-only validation | Хороший UX. | Custom clients могут bypass it. |
| Hub-only validation | Strong boundary. | Worse UX без previews/confirmations. |
| No destructive actions | Safer surface. | Operators возвращаются к dangerous ad hoc SQL. |

### Дополнительные вопросы

**Какое action самое рискованное?**  
Bulk purge, потому что он permanently destroys recovery evidence.

**Зачем validate JSON в hub?**  
Чтобы не сохранять edited payloads, которые потом нельзя dispatch'ить.

**Зачем log actor identity?**  
Operator actions - часть incident evidence и audit.
