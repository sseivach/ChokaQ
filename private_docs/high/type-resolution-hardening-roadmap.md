# Type Resolution Hardening Roadmap

This file tracks the type-resolution hardening work for ChokaQ. It is
intentionally private and not linked from the public documentation navigation.

This roadmap is separate from expression-tree dispatch. Expression trees make
handler invocation fast after the job type and handler are already known. They
do not solve the cost or safety of finding the CLR job type from a persisted
type key.

Priority: high.

Reason: persisted type identity is part of the public NuGet contract. Short-name
fallbacks and runtime type scans can resolve the wrong job or fail after normal
refactors, so this is a real correctness and supportability risk rather than a
hyperscale optimization.

## Current State

ChokaQ already has the right foundation:

- `JobTypeRegistry` maps persisted job keys to CLR types.
- SQL enqueue uses the registry key when a job is registered.
- In-memory enqueue uses the registry key when a job is registered.
- `BusJobDispatcher` resolves registered job keys through `JobTypeRegistry`.
- Handler invocation is compiled and cached through expression trees.

The remaining risks are fallback paths:

- SQL enqueue falls back to `job.GetType().Name` for unregistered jobs.
- In-memory enqueue falls back to `job.GetType().Name` for unregistered jobs.
- In-memory DLQ requeue tries registry first, then `Type.GetType`, then a full
  AppDomain assembly/type scan.
- The full scan matches `t.Name == job.Type`, which can resolve the wrong type
  when two classes share the same short name.
- The fallback scan is in an operator runtime path and can repeat for every job
  in a batch requeue.

## Problem Statement

These are two separate problems:

| Concern | Current Mechanism | Risk |
|---|---|---|
| Find CLR job type | Registry, `Type.GetType`, AppDomain scan fallback | Slow and ambiguous when fallback is used. |
| Invoke handler | Cached expression-tree delegate | Already fast for the current scope. |

The expensive code path is:

```csharp
AppDomain.CurrentDomain.GetAssemblies()
    .SelectMany(a => a.GetTypes())
    .FirstOrDefault(t => t.Name == job.Type);
```

This is costly because `GetTypes()` walks metadata for loaded assemblies,
allocates arrays, can throw `ReflectionTypeLoadException`, and scales with the
number of assemblies and types in the host application. It is also unsafe
because matching only by short type name is ambiguous.

## Design Principles

- Registered profile keys are the production path.
- Runtime type lookup must be O(1) for normal job processing and operator
  recovery.
- Persisted type identity must not depend on CLR short names.
- Ambiguous type resolution should fail clearly instead of choosing a random
  type.
- Reflection scans, if kept at all, should happen once at startup and populate a
  cache, not inside per-job workflows.
- Expression-tree dispatch remains the right handler-invocation strategy after
  resolution succeeds.

## Status Legend

| Status | Meaning |
|---|---|
| Done | Implemented, tested, and documented for current scope. |
| Partial | Some behavior exists, but the acceptance criteria below are not complete. |
| Open | Not implemented yet. |
| Deferred | Intentionally postponed until the registry path is hardened. |

## Executive Sequence

1. Keep `JobTypeRegistry` as the source of truth for registered jobs.
2. Remove short-name fallback from persisted type identity.
3. Remove per-job AppDomain scans from requeue paths.
4. Decide whether unregistered jobs are rejected or persisted by full identity.
5. Add startup validation for duplicate keys and ambiguous fallbacks.
6. Add tests for same short-name classes in different namespaces.
7. Document the type-key contract and migration strategy.

## Phase A: Separate Resolution From Dispatch

Goal: make the architecture explicit so future changes do not confuse handler
invocation performance with type lookup performance.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| A.1 | P0 | Done | Cache handler invocation delegates. | `BusJobDispatcher` uses compiled expression-tree delegates instead of reflection invoke. |
| A.2 | P0 | Done | Use registry before fallback resolution. | Dispatcher resolves `jobType` through `JobTypeRegistry` before `Type.GetType`. |
| A.3 | P1 | Open | Document resolution vs invocation. | Internal or public docs explain that registry lookup and expression-tree invocation solve different problems. |
| A.4 | P2 | Open | Add code comments at fallback boundaries. | Comments identify fallback type resolution as compatibility behavior, not the normal production path. |

Target framing:

> Expression trees optimize handler invocation. Type resolution must be solved
> separately through stable keys and dictionary lookup.

## Phase B: Registry-First Production Path

Goal: make registered job profiles the complete, O(1), production-grade type
resolution path.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| B.1 | P0 | Done | Add `JobTypeRegistry`. | Registry has key-to-type and type-to-key dictionaries. |
| B.2 | P0 | Done | Register profile keys at startup. | Profile registration populates the registry before workers execute jobs. |
| B.3 | P0 | Done | Use registry in SQL enqueue. | Registered SQL jobs persist the configured profile key. |
| B.4 | P0 | Done | Use registry in in-memory enqueue. | Registered in-memory jobs persist the configured profile key. |
| B.5 | P0 | Done | Use registry in dispatcher. | Registered persisted keys resolve without reflection scan. |
| B.6 | P1 | Done | Use registry in in-memory requeue. | DLQ requeue tries `JobTypeRegistry` before fallbacks. |
| B.7 | P1 | Open | Expose strict registry mode. | Hosts can require every Bus job type to be explicitly registered before enqueue or requeue. |
| B.8 | P1 | Open | Add registry diagnostics. | Startup logs registered keys and rejects ambiguous or duplicate registrations clearly. |

## Phase C: Remove Short-Name Persistence

Goal: prevent two job classes with the same short name from colliding.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| C.1 | P1 | Partial | Avoid short names for registered jobs. | Registered jobs are safe, but unregistered fallback still uses `job.GetType().Name`. |
| C.2 | P1 | Open | Replace enqueue fallback identity. | Unregistered fallback uses `job.GetType().FullName`, `AssemblyQualifiedName`, or strict rejection. |
| C.3 | P1 | Open | Replace short-name scan match. | No runtime path resolves with `t.Name == job.Type`. |
| C.4 | P1 | Open | Add duplicate short-name regression tests. | Two jobs named `SendEmailJob` in different namespaces cannot resolve incorrectly. |
| C.5 | P2 | Open | Add migration note. | Docs explain how existing rows with short type names should be handled before upgrading. |

Preferred policy:

- Production Bus mode should require explicit profile keys.
- Compatibility mode may support fully qualified CLR names.
- Short CLR names should not be used for newly persisted jobs.

## Phase D: Eliminate Runtime AppDomain Scans

Goal: remove `GetAssemblies().SelectMany(GetTypes)` from per-job operator and
execution paths.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| D.1 | P0 | Open | Remove AppDomain scan from `RequeueJobFromStorage`. | Batch requeue never scans all loaded types per job. |
| D.2 | P1 | Open | Add type-resolution cache if compatibility fallback remains. | Any fallback resolution uses a `ConcurrentDictionary<string, Type?>`. |
| D.3 | P1 | Open | Build fallback cache once at startup. | If assembly scanning is supported, it runs during startup validation and handles loader exceptions safely. |
| D.4 | P1 | Open | Handle `ReflectionTypeLoadException`. | Partial type-load failures do not crash a requeue path unpredictably. |
| D.5 | P2 | Open | Add batch requeue performance test. | Requeueing many DLQ jobs performs O(number of jobs) dictionary lookups, not O(number of jobs * loaded types). |

Minimum acceptable compatibility fix:

```csharp
private static readonly ConcurrentDictionary<string, Type?> TypeCache = new();

var jobType = TypeCache.GetOrAdd(job.Type, key =>
    _registry.GetTypeByKey(key) ?? Type.GetType(key));
```

Preferred fix:

```csharp
var jobType = _registry.GetTypeByKey(job.Type);
if (jobType is null)
{
    throw new InvalidOperationException(
        $"Unknown job type key '{job.Type}'. Register the job type through a ChokaQ profile.");
}
```

## Phase E: Requeue And Resurrection Safety

Goal: make operator recovery safe, predictable, and cheap.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| E.1 | P1 | Partial | Resolve requeued jobs through registry first. | Current code does this before fallback scanning. |
| E.2 | P1 | Open | Fail unknown requeue type clearly. | If a DLQ job cannot be resolved, operator sees a clear error instead of silent partial batch behavior. |
| E.3 | P1 | Open | Add per-job batch result for requeue. | Bulk requeue reports succeeded, skipped, unknown-type, and failed counts. |
| E.4 | P2 | Open | Add operator guidance for old rows. | Docs explain how to handle DLQ rows whose type key is no longer registered. |
| E.5 | P2 | Open | Add migration hook for type keys. | Optional mapping can translate old keys to new keys during requeue. |

## Phase F: Public Type-Key Contract

Goal: make persisted job identity a deliberate API contract.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| F.1 | P1 | Partial | Explain profile keys in getting started docs. | Docs mention `typeKey`, but should also state why short CLR names are unsafe. |
| F.2 | P1 | Open | Add type-key rules. | Docs define uniqueness, versioning, migration, and backward compatibility expectations. |
| F.3 | P1 | Open | Recommend semantic keys. | Examples use stable keys such as `email.send.v1` instead of class names. |
| F.4 | P2 | Open | Add incompatible payload version guidance. | Docs recommend new keys for breaking payload schema changes. |
| F.5 | P2 | Open | Add troubleshooting section. | Docs explain `Unknown Job Type` errors and how to fix missing registrations. |

Suggested contract:

```markdown
Job type keys are persisted data. Treat them like message-contract names, not
like implementation class names. Use explicit profile keys and version them when
payload compatibility breaks.
```

## Phase G: Tests

Goal: prove type resolution is fast and unambiguous.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| G.1 | P1 | Open | Add duplicate short-name tests. | Two job DTOs with the same `Name` but different namespace cannot collide. |
| G.2 | P1 | Open | Add strict-registration tests. | Enqueue or requeue fails clearly when strict mode is enabled and a job is not registered. |
| G.3 | P1 | Open | Add batch requeue resolution tests. | Bulk DLQ requeue resolves via registry without scanning assemblies per row. |
| G.4 | P2 | Open | Add legacy fallback tests. | If compatibility fallback remains, it resolves `FullName` or `AssemblyQualifiedName`, not short names. |
| G.5 | P2 | Open | Add loader-exception tests. | Fallback startup scanning handles partial type-load failures safely. |

## Suggested Implementation Order

1. Add tests that reproduce short-name collision and batch requeue fallback cost.
2. Add strict registry mode and make it the recommended production setting.
3. Change unregistered enqueue fallback from short name to full identity or
   rejection.
4. Remove `AppDomain.GetAssemblies().SelectMany(GetTypes)` from
   `RequeueJobFromStorage`.
5. Add clear unknown-type results for operator requeue.
6. Update docs to treat job type keys as persisted message-contract names.
7. Add migration guidance for existing short-name rows.

## Non-Goals

- Do not replace expression-tree dispatch; it already solves handler invocation
  overhead.
- Do not use reflection scans as a normal production lookup strategy.
- Do not promise automatic recovery for jobs whose CLR type no longer exists.
- Do not make CLR class names the recommended persisted contract.
