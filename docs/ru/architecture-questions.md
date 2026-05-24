# Architecture Q&A

![Architecture questions map](/diagrams/62-architecture-questions-map.png)

Этот guide готовит ChokaQ к senior architecture review или system-design
discussion. Цель не в том, чтобы запомнить trivia реализации. Цель - показать,
что у каждого важного design есть причина, цена и recovery story.

## One-minute pitch

ChokaQ - .NET background job engine на SQL Server. Он хранит active jobs в
`JobsHot`, successful jobs в `JobsArchive`, а failed/operator-held jobs в
`JobsDLQ`. Workers claim rows через SQL locking, обрабатывают их через typed
или raw dispatcher и финализируют state через atomic SQL transitions. The Deck
дает real-time operational inspection и repair workflows.

## Дополнительные архитектурные вопросы

### Почему SQL Server?

Потому что продукт оптимизируется под durable business jobs, inspectability и
operator repair. SQL дает transactional state, знакомые backup/restore,
queryable evidence и простой deployment story для команд, которые уже используют
SQL.

Trade-off: SQL становится central throughput boundary. Если workload требует
massive streaming throughput, broker/log может быть лучше.

### Почему не exactly-once?

Exactly-once external side effects - не то, что queue engine может обещать для
произвольных handlers. ChokaQ обещает at-least-once execution и дает tools для
idempotency, DLQ, heartbeat и recovery.

### Почему Hot/Archive/DLQ вместо одной table?

Active fetch не должен конкурировать с годами history. Split держит hot worker
path маленьким, сохраняя audit и repair data.

### Что происходит при worker crash?

Fetched jobs, которые не начали user code, могут вернуться в pending.
Processing jobs с expired heartbeat переносятся в DLQ как zombies, потому что
side effects уже могли произойти.

### Зачем The Deck?

Durable queue без operator surface выталкивает recovery в ad hoc SQL. The Deck
превращает repair в authenticated, bounded и observable workflows.

## Что нужно честно признавать

- SQL не обгонит dedicated broker для огромных streaming workloads.
- At-least-once требует handler idempotency.
- Dashboard actions требуют серьезной authorization.
- Prefetching добавляет stale-ownership window, который нужно guard.
- Retention и DLQ policy - operational responsibilities.

## Сильное завершение

ChokaQ production-oriented, потому что он не прячет failure. Он хранит state,
classifies failure, exposes operations и проводит dangerous actions через
explicit control points.
