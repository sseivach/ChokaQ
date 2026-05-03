# Release And Package Strategy

This document records packaging decisions without turning ChokaQ into a published
NuGet product yet. The project is still moving quickly, especially around The
Deck and operator workflows, so the current goal is architectural readiness: keep
boundaries clean, document the intended shape, and avoid premature packaging
work that would create churn.

## Current Decision

ChokaQ is not packaged or published as NuGet yet.

The preview line targets `net10.0` only. Older target frameworks are explicitly
out of scope for the current development line. Supporting `net8.0` or other LTS
runtimes would multiply the test matrix, package compatibility work, and
framework-specific edge cases before the product surface is stable.

## Intended Package Shape

When the project is ready to publish, the first public package should be a single
package:

```text
ChokaQ
```

The package should include the current complete product surface:

- abstractions;
- core engine;
- SQL Server storage;
- The Deck dashboard;
- health checks;
- runtime configuration model;
- README and sample configuration.

This is a user-experience decision. ChokaQ is currently one product, not an
ecosystem of optional providers. Installing one package should give the host app
the full supported experience instead of forcing users to discover missing
pieces by trial and error.

## Deferred Split

Provider-specific packages can make sense later, but only when there is real
choice:

```text
ChokaQ
ChokaQ.Storage.SqlServer
ChokaQ.Storage.PostgreSql
ChokaQ.Storage.Redis
```

Until there is more than one production storage provider, splitting SQL Server
into a separate public package is package fragmentation without user value.
The solution can keep internal projects for engineering boundaries, while the
published package boundary should follow the install experience.

The Deck can also be split later if it becomes a large optional surface. For now
it is part of the ChokaQ operator experience and should ship with the product.

## Release Readiness Gate

The operational checklist lives in [Release Checklist](/release-checklist).
That page is the source of truth for build, test, docs, Docker smoke, security,
database, and future package gates.

Before publishing any NuGet package, ChokaQ should have:

- stable public API names and configuration sections;
- README that describes implemented behavior, not aspirational behavior;
- architecture docs that clearly separate implemented guarantees from planned
  learning/book material;
- package metadata, license, symbols, SourceLink, and package README;
- `dotnet build` passing without warnings;
- unit tests passing;
- SQL integration tests passing in CI;
- sample app smoke test;
- documented upgrade and schema migration story.

Actual pack/publish work is intentionally deferred until those gates are closer.
