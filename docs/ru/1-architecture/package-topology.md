# Package Topology

![Package topology](/diagrams/01-package-topology.png)

Большинство application hosts должны ссылаться на один пакет:

```xml
<PackageReference Include="ChokaQ" Version="0.1.0-preview.1" />
```

Верхнеуровневый пакет - product entry point. Он собирает runtime pieces вместе,
чтобы consumers не должны были изучать internal package graph до запуска
первого job.

## Runtime packages

| Package | Role | Consumer expectation |
|---|---|---|
| `ChokaQ` | Верхнеуровневый package. | Устанавливайте его в обычное приложение. |
| `ChokaQ.Abstractions` | Public contracts, DTOs, enums, storage и worker interfaces. | Подтягивается транзитивно. Полезен на integration boundaries. |
| `ChokaQ.Core` | Runtime engine: registration, dispatch, processing, middleware, idempotency, metrics, health, resilience. | Подтягивается top-level package graph. |
| `ChokaQ.Storage.SqlServer` | Durable SQL Server provider: schema bootstrap, storage operations, SQL worker, migrations, SQL health checks. | Включен для default durable backend. |
| `ChokaQ.TheDeck` | Operational dashboard и SignalR notification surface. | Включен, чтобы operators могли inspect и control runtime state. |

## Зачем top-level package?

Package topology отделяет architecture от user friction.

Внутри ChokaQ нужны чистые boundaries. Storage не должен владеть job contracts.
The Deck не должен владеть processing. Core engine не должен зависеть от
конкретного database provider. Эти boundaries держат код testable и оставляют
путь для будущих providers.

Снаружи первый install path должен быть простым. User не должен решать, какие
internal runtime packages нужны, чтобы просто обработать job. Top-level package
`ChokaQ` - curated product experience.

## Dependency direction

Стабильное направление dependencies:

```text
Application
  -> ChokaQ
      -> ChokaQ.Storage.SqlServer
          -> ChokaQ.Core
              -> ChokaQ.Abstractions
      -> ChokaQ.TheDeck
          -> ChokaQ.Abstractions
```

Точный NuGet dependency graph может эволюционировать, но архитектурное правило
стабильно: contracts находятся внизу, runtime orchestration выше contracts,
storage реализует storage contracts, а The Deck наблюдает и управляет через
public runtime surfaces.

## Source samples vs package consumers

Repository samples могут использовать project references во время development.
Это намеренно: локальные изменения видны без упаковки NuGet packages после
каждого edit.

Package-consumer validation использует `samples/ChokaQ.Sample.NuGetLab`. Этот
sample восстанавливает `ChokaQ` через NuGet и доказывает, что реальный host
может установить top-level package и получить нужные transitive runtime assets.

## Архитектурное решение

### Почему выбран такой pattern?

Top-level package дает consumers один основной install target, а subpackages
сохраняют internal ownership. Это распространенная форма зрелых .NET libraries:
простой default package плюс lower-level packages для advanced integrations.

### Trade-offs

Top-level package может подтянуть больше, чем нужно минимальному app. Например,
headless worker может не нуждаться в The Deck. Для preview product это
осознанный trade-off: operational visibility является частью value proposition,
а один install path снижает setup mistakes.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| Просить users устанавливать каждый subpackage | Maximum explicitness. | Higher onboarding friction и больше setup combinations для validation. |
| Публиковать один monolithic assembly | Very simple packaging. | Слабые module boundaries и более трудная работа над future providers. |
| Раздельные install guides для `ChokaQ.SqlServer` и `ChokaQ.Dashboard` | More control for advanced users. | New users слишком рано должны понимать topology. |

### Когда не использовать такой подход

Для long-term stable release может быть полезно добавить меньший headless
package для hosts, которые не хотят dashboard assets. Это должно быть additive,
а не replacement для default `ChokaQ` package.

### Дополнительные вопросы

**Зачем держать больше одного internal package?**  
Потому что internal package boundaries выражают ownership: abstractions, core
runtime, storage provider и dashboard меняются по разным причинам.

**Почему dashboard включен по умолчанию?**  
Потому что durable queue без operator surface скрывает важнейшее production
state. The Deck - часть runtime story, а не demo accessory.

**Как добавить PostgreSQL позже?**  
Добавить provider package, который реализует storage contracts, оставить core
engine выше provider boundary и отдельно решить, должен ли top-level package
включать его или давать explicit opt-in.
