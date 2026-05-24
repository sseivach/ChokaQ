# Paging, Sorting And Filtering

![Dashboard paging sorting filtering](/diagrams/56-dashboard-paging-sorting-filtering.png)

History views The Deck могут inspect Archive и DLQ, не превращая dashboard в unbounded SQL scan.

Paging, sorting и filtering являются частью operational safety model.

## Paging flow

1. UI создает `HistoryFilterDto`.
2. Dashboard нормализует page number и size.
3. Storage строит filtered count query.
4. Storage строит paged data query.
5. SQL использует `OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY`.
6. UI clamp'ит page после destructive operations, если current page больше не exists.

## Sorting safety

SQL sort columns whitelist'ятся через `SqlSortBuilder`. User-provided sort names не становятся raw SQL column names.

Supported sort keys map'ятся на известные columns:

- `Id`;
- `Queue`;
- `Type`;
- `CreatedAtUtc`;
- Archive `FinishedAtUtc`;
- DLQ `FailedAtUtc`;
- `FailureReason`;
- `AttemptCount`.

Unknown sort keys fall back к default date column.

## Filtering

Filters могут target queue, type, failure reason, date ranges и search term. DLQ bulk filters нормализуются на hub boundary перед storage.

## Архитектурное решение

### Почему этот pattern?

Operators нужна searchable history, но history tables могут стать большими. Dashboard должен page и whitelist queries, чтобы inspection оставался safe.

### Trade-offs

Search намеренно bounded и operational, а не full analytics engine. Deep reporting должен использовать dedicated analytics/export pipelines.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| Load all history | Simple UI. | Ломается на production scale. |
| Raw SQL sort/filter from UI | Flexible. | Injection и performance risk. |
| External search index | Powerful. | Extra infrastructure и consistency model. |

### Дополнительные вопросы

**Зачем whitelist sort columns?**  
Потому что dynamic `ORDER BY` нельзя parameterize'ить как values.

**Зачем clamp page после purge/requeue?**  
Bulk actions могут удалить current page. UI должен перейти на valid page, а не показывать confusing empty state.

**Это analytics layer?**  
Нет. Это operational inspection.
