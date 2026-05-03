# Release Checklist

This checklist is the operational gate for deciding whether a ChokaQ build is
ready to call a release candidate, production-preview candidate, or future NuGet
candidate.

ChokaQ is not published as NuGet yet. The current checklist exists to make the
project release-ready without forcing package work before the API and The Deck
settle.

## Release Levels

Use the smallest level that matches the decision being made:

| Level | Use When | Required Gates |
|---|---|---|
| Development checkpoint | Checking whether the current branch is healthy enough to continue. | Build, unit tests, docs build. |
| Production-preview candidate | Asking whether the source distribution and Docker sample are safe for serious evaluation. | All gates except future NuGet publish. |
| Future NuGet candidate | Publishing becomes active work later. | All gates, including package metadata, pack validation, local install smoke, and publish approval. |

## Stop-The-Line Rules

Do not call the build release-ready if any of these are true:

- solution build has warnings;
- unit tests fail;
- SQL integration tests are skipped because Docker was unavailable;
- sample app cannot start against SQL Server;
- `/health` does not report a healthy SQL sample;
- README or docs describe planned behavior as already implemented;
- sample configuration contains real-looking secrets;
- roadmap status does not match the code and docs;
- future package work starts before an explicit decision to publish NuGet.

These rules are intentionally strict. A background processor is infrastructure:
small release mistakes become operator pain later.

## 1. Preflight

Run from the repository root.

```powershell
dotnet --info
docker version
npm --prefix docs --version
git status --short
```

Checklist:

- .NET SDK is the intended `net10.0` SDK.
- Docker is running.
- Node/npm can run the docs build.
- Worktree changes are understood and intentionally included.
- No unrelated local files are being treated as release artifacts.

Why this exists: release validation is only useful when the environment is
known. A missing Docker daemon or wrong SDK can create false confidence or false
failures.

## 2. Source Truth Gate

Read the public entry points:

- `README.md`
- `docs/getting-started.md`
- `docs/configuration.md`
- `docs/release-strategy.md`
- `docs/roadmap.md`
- `docs/roadmap.ru.md`

Checklist:

- Current status says active development / production-preview hardening.
- Docs do not claim a published NuGet package.
- Package strategy still says one future `ChokaQ` package unless that decision
  has explicitly changed.
- Phase/status tables match the implemented behavior.
- Frozen work is clearly marked as frozen.
- Any planned behavior is labeled as planned, future, or frozen.

Useful scan:

```powershell
rg "production-ready|enterprise-grade|zero deadlocks|No extra packages|published as NuGet|shipped as NuGet" README.md docs
```

Why this exists: documentation is part of the product surface. If docs overclaim
guarantees, the release is already misleading even when the code compiles.

## 3. Build Gate

```powershell
dotnet restore ChokaQ.sln
dotnet build ChokaQ.sln --configuration Release --no-restore
```

Checklist:

- Build exits with code `0`.
- Warning count is `0`.
- No project relies on local machine-only paths or SDK fallbacks.

Why this exists: warnings are often the first visible sign of API drift,
nullable mistakes, dead code, or generated artifacts that no longer match the
source.

## 4. Unit Test Gate

```powershell
dotnet test ChokaQ.sln --configuration Release --no-build --filter "Category=Unit"
```

Checklist:

- Unit-only suite passes.
- No test is skipped unless the skip is intentional and documented.
- Test output does not contain hidden build warnings.

Why this exists: unit tests are the fast correctness net. They should catch
contract regressions before slower SQL and Docker checks spend time.

## 5. SQL Integration Gate

Docker must be available for this gate.

```powershell
dotnet test ChokaQ.sln --configuration Release --no-build --filter "Category=Integration"
```

Checklist:

- Full suite passes.
- SQL integration tests run against a real SQL Server container.
- Performance baseline tests run and stay within their budgets.
- Schema initialization tests prove provisioning remains idempotent.
- Query consistency tests still restrict `NOLOCK` to passive telemetry paths.

Why this exists: ChokaQ's strongest guarantees live in SQL behavior. Mock-only
validation cannot prove fetch locking, transactional moves, schema provisioning,
or query shape under real SQL Server semantics.

## 6. Docs Gate

```powershell
npm --prefix docs run docs:build
```

Checklist:

- VitePress build succeeds.
- New pages are linked from navigation where appropriate.
- Generated `.vitepress/.temp` output is not committed.

Why this exists: docs are now a release artifact. Broken docs navigation means
new users and future maintainers lose the path through the system.

## 7. Docker Compose Smoke Gate

```powershell
docker compose config
docker compose up --build
```

In a second terminal:

```powershell
Invoke-WebRequest http://localhost:5299/health
```

Manual checks:

- `http://localhost:5299` opens the sample launcher.
- `http://localhost:5299/chokaq` opens The Deck for the sample.
- `http://localhost:5299/health` is healthy.
- sample logs show SQL schema initialization or successful reuse.
- stopping the app does not leave obvious unhandled shutdown exceptions.

Cleanup:

```powershell
docker compose down -v
```

Why this exists: a release candidate must be runnable by someone who is not
inside the IDE. The compose sample is the closest thing to an external user
experience before NuGet exists.

## 8. Database And Migration Gate

Checklist:

- `SchemaMigrations` is created when auto-provisioning is enabled.
- Current schema version is recorded exactly once.
- Startup can run twice against the same database without destructive changes.
- Hot, Archive, DLQ, StatsSummary, Queues, and SchemaMigrations tables exist.
- Fetch, recovery, history, DLQ, and dashboard indexes exist.

Why this exists: background processors accumulate state. Release readiness means
the next startup should be boring, repeatable, and explainable to an operator.

## 9. Security And Operations Gate

Checklist:

- The Deck requires authorization by default.
- Anonymous dashboard access is only possible through explicit
  `AllowAnonymousDeck = true`.
- Destructive commands use the destructive policy when configured.
- Hub commands reject malformed IDs, oversized payloads, unsafe batches, and
  invalid queue names.
- Operator mutations write real actor identity when authentication is present.
- Health checks, metrics, and structured log EventIds are documented.
- Metric cardinality caps are enabled and configurable.

Why this exists: The Deck is not just a UI. It is an administrative control
plane that can edit, requeue, purge, pause, and cancel work.

## 10. Future NuGet Gate

This gate is frozen until NuGet publishing becomes active work.

When unfrozen, require:

- explicit version decision;
- package metadata;
- license metadata;
- README packaged into the artifact;
- symbols and SourceLink;
- local `dotnet pack` validation;
- install smoke test from a local package folder into a clean sample app;
- final human approval before publish.

Do not use this gate as a reason to publish early. It is a placeholder for the
future, not a current release task.

## Sign-Off Record

For each serious release candidate, record:

| Field | Value |
|---|---|
| Candidate name |  |
| Commit or snapshot |  |
| Date |  |
| Build result |  |
| Unit test result |  |
| SQL integration result |  |
| Docs build result |  |
| Docker smoke result |  |
| Known exceptions |  |
| Approved by |  |

Why this exists: release memory matters. Six months later, the project should be
able to answer what was validated, what was skipped, and who accepted the risk.
