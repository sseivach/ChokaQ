# Package Topology

![Package topology](/diagrams/01-package-topology.png)

Most application hosts should reference one package:

```xml
<PackageReference Include="ChokaQ" Version="0.1.0-preview.1" />
```

The top-level package is the product entry point. It brings the runtime pieces
together so consumers do not need to learn the internal package graph before
running their first job.

## Runtime Packages

| Package | Role | Consumer expectation |
|---|---|---|
| `ChokaQ` | Top-level package. | Install this in a normal application. |
| `ChokaQ.Abstractions` | Public contracts, DTOs, enums, storage and worker interfaces. | Referenced transitively. Useful for integration boundaries. |
| `ChokaQ.Core` | Runtime engine: registration, dispatch, processing, middleware, idempotency, metrics, health, resilience. | Pulled by the top-level package graph. |
| `ChokaQ.Storage.SqlServer` | Durable SQL Server provider: schema bootstrap, storage operations, SQL worker, migrations, SQL health checks. | Included for the default durable backend. |
| `ChokaQ.TheDeck` | Operational dashboard and SignalR notification surface. | Included so operators can inspect and control runtime state. |

## Why A Top-Level Package?

The package topology separates architecture from user friction.

Internally, ChokaQ needs clean boundaries. Storage should not own job contracts.
The Deck should not own processing. The core engine should not depend on a
specific database provider. Those boundaries keep the code testable and leave a
path for future providers.

Externally, the first install path should be simple. A user should not have to
decide which internal runtime packages are required just to process a job. The
top-level `ChokaQ` package is the curated product experience.

## Dependency Direction

The stable dependency direction is:

```text
Application
  -> ChokaQ
      -> ChokaQ.Storage.SqlServer
          -> ChokaQ.Core
              -> ChokaQ.Abstractions
      -> ChokaQ.TheDeck
          -> ChokaQ.Abstractions
```

The exact NuGet dependency graph may evolve, but the architectural rule is
stable: contracts sit at the bottom, runtime orchestration sits above contracts,
storage implements storage contracts, and The Deck observes/controls through
public runtime surfaces.

## Source Samples vs Package Consumers

Repository samples may use project references during development. That is
intentional: it makes local changes visible without packing NuGet packages on
every edit.

Package-consumer validation uses `samples/ChokaQ.Sample.NuGetLab`. That sample
restores from `artifacts/packages` and proves that a real host can install the
top-level package and get the transitive runtime assets it needs.

## Architecture Decision

### Why this pattern?

The top-level package gives consumers one primary install target while the
subpackages preserve internal ownership. This is the same product shape used by
many mature .NET libraries: a simple default package plus lower-level packages
for advanced integrations.

### Trade-offs

The top-level package can pull more than a minimal app needs. For example, a
headless worker may not need The Deck. The trade-off is deliberate for the
preview product: operational visibility is part of the value proposition, and a
single install path reduces setup mistakes.

### Alternatives considered

| Alternative | Benefit | Cost |
|---|---|---|
| Ask users to install every subpackage | Maximum explicitness. | Higher onboarding friction and more setup combinations to validate. |
| Publish only one monolithic assembly | Very simple packaging. | Weak module boundaries and harder future provider work. |
| Separate `ChokaQ.SqlServer` and `ChokaQ.Dashboard` install guides | More control for advanced users. | New users must understand topology too early. |

### When not to use this approach

For a long-term stable release, it may be useful to add a smaller headless
package for hosts that do not want dashboard assets. That should be additive,
not a replacement for the default `ChokaQ` package.

### Interview questions

**Why keep more than one internal package?**  
Because internal package boundaries express ownership: abstractions, core
runtime, storage provider, and dashboard evolve for different reasons.

**Why include dashboard by default?**  
Because a durable queue without an operator surface hides the most important
production state. The Deck is part of the runtime story, not a demo accessory.

**How would you add PostgreSQL later?**  
Add a provider package that implements storage contracts, keep the core engine
above the provider boundary, and decide whether the top-level package should
include it or expose it as an explicit opt-in.
