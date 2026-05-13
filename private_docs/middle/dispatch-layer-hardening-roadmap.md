# Dispatch Layer Hardening Roadmap

This file tracks hardening work for `BusJobDispatcher`, `PipeJobDispatcher`,
middleware execution, handler delegate caching, and registry-driven dispatch. It
is intentionally private and not linked from the public documentation
navigation.

Priority: middle.

Reason: the current dispatch layer is architecturally strong and already uses
DI scopes, registry-first type lookup, middleware chaining, and compiled
expression delegates. The remaining work is mostly about stricter contracts,
payload compatibility, diagnostics, and future-proofing rather than an immediate
execution correctness blocker.

## Current State

Strong parts already implemented:

- Bus mode creates a DI scope per job execution.
- Bus mode resolves registered job keys through `JobTypeRegistry` first.
- Bus mode compiles and caches expression-tree delegates instead of using
  reflection invoke on every execution.
- Bus mode builds an ASP.NET-style middleware pipeline around the handler.
- `ChokaQOptions.AddMiddleware<TMiddleware>()` gives hosts a public registration
  hook for cross-cutting handler concerns.
- Pipe mode is intentionally minimal and calls one registered
  `IChokaQPipeHandler`.
- Pipe mode also supports the same middleware abstraction.
- `JobTypeRegistry` has key-to-type and type-to-key maps.
- Duplicate registry keys fail fast.
- Reverse lookup intentionally keeps the first key when one CLR type is mapped
  to multiple keys.

Remaining risks:

- `BusJobDispatcher` still falls back to `Type.GetType(jobType)` when registry
  lookup misses.
- Bus deserialization uses direct `JsonSerializer.Deserialize(payload, clrType)`
  without shared ChokaQ serializer options.
- Enqueue and dispatch serialization are not yet centralized behind one
  compatibility contract.
- Dispatch does not cache `JsonTypeInfo` or use source-generated metadata, so
  high-throughput Bus mode can pay avoidable serialization reflection cost.
- Handler delegate cache is keyed by closed handler service type only. This is
  acceptable for the current `IChokaQJobHandler<TJob>` model, but a
  `(handlerType, jobType)` key would be clearer and safer if dispatch supports
  more handler shapes later.
- Handler method lookup uses `GetMethod("HandleAsync")`, which is acceptable for
  the current single-method interface but fragile if future handler models allow
  overloads, explicit interface implementations, or alternate method shapes.
- `BusJobDispatcher` still catches `TargetInvocationException` in the compiled
  delegate path. Expression-tree calls should surface handler exceptions
  directly, so this catch is likely legacy defensive code.
- Middleware receives a deserialized DTO in Bus mode and a raw payload string in
  Pipe mode. That is documented in the interface, but it makes reusable
  middleware harder to write.
- `PipeJobDispatcher` manually checks a nullable handler instead of using
  `GetRequiredService`; the current explicit exception is fine but less
  consistent with Bus mode.
- Pipe mode has limited dispatch diagnostics around job type and payload
  handoff.
- Pipe mode is intentionally raw-message oriented and gives up Bus-mode
  compile-time payload safety. Optional deserialization/validation hooks may be
  needed if hosts use Pipe mode for structured dynamic jobs.
- Job type versioning and schema evolution are conventions, not enforced
  contracts.
- Per-job DI scopes are correct, but high-throughput scope cost has not been
  measured.
- The current middleware pipeline is a dispatch/handler pipeline, not a full
  lifecycle pipeline. Middleware does not own fetch, lease acquisition, circuit
  breaker decisions, retry scheduling, archival, or DLQ transitions.
- The public API is global registration via `AddMiddleware<TMiddleware>()`, not
  a fluent per-job or per-profile API such as
  `UseLogging().UseOpenTelemetry()`.
- Per-job, per-type, per-queue, and per-profile middleware selection is not
  modeled yet.
- Retry behavior is centralized in the processor and storage policy layers.
  Allowing arbitrary middleware to retry the handler could conflict with attempt
  counts, circuit breaker state, DLQ policy, and idempotency guarantees.

## Design Principles

- Registry keys are persisted message-contract names, not CLR implementation
  details.
- Expression-tree dispatch optimizes invocation after type resolution succeeds;
  it does not replace strict type-key policy.
- Serializer behavior must be identical between enqueue, storage, requeue, and
  dispatch.
- Middleware should receive a stable execution envelope when it needs behavior
  that is portable across Bus and Pipe mode.
- Per-job scopes are the safe default. Optimize scope lifetime only after
  measurement.
- The dispatch layer should fail clearly when registration, payload, or handler
  contracts are broken.
- Handler middleware is appropriate for logging, tracing, audit tagging,
  validation, profiling, and idempotency guards.
- Retry scheduling, DLQ transitions, ownership checks, and archival are core
  lifecycle decisions and should stay in the processor/state layer unless they
  are exposed through a deliberate strategy interface.
- Do not market the current middleware hook as a full lifecycle pipeline unless
  the docs clearly define its scope.

## Status Legend

| Status | Meaning |
|---|---|
| Done | Implemented, tested, and documented for current scope. |
| Partial | Some behavior exists, but the acceptance criteria below are not complete. |
| Open | Not implemented yet. |
| Deferred | Intentionally postponed until stricter contracts are needed. |

## Executive Sequence

1. Keep the current expression-tree delegate dispatch path.
2. Remove or gate `Type.GetType` fallback behind explicit compatibility mode.
3. Add shared serializer options or a serializer abstraction.
4. Add dispatch tests for serializer options, unknown type keys, and handler
   resolution failures.
5. Decide whether middleware needs a stable execution envelope.
6. Document that the current middleware pipeline wraps handler dispatch, not the
   entire job lifecycle.
7. Document registry reverse-lookup behavior and type-key versioning.
8. Remove legacy exception unwrapping if tests prove expression-tree dispatch
   surfaces handler exceptions directly.
9. Measure per-job DI scope overhead before optimizing scope lifetime.

## Latest Audit Classification

| Finding | Current Status | Action |
|---|---|---|
| DI scope per job is correct. | Valid strength. It isolates scoped dependencies per execution. | Preserve unless profiling proves it is too expensive. |
| Handler delegate cache should use `(handlerType, jobType)`. | Partially valid. Current key is a closed `IChokaQJobHandler<TJob>` service type, so it already includes the job type for today's model. | Optional future-proofing; add tests before changing. |
| Handler method lookup with `GetMethod("HandleAsync")` is fragile. | Valid future-proofing concern. The current interface is simple, but the lookup should be precise if handler shapes evolve. | Use signature-based lookup or interface mapping tests before supporting overloads or explicit implementation. |
| `Type.GetType(jobType)` is risky. | Valid. It is a fallback path and can break under refactors, trimming, AOT, or missing assembly-qualified names. | Prefer strict registry mode; track with type-resolution roadmap. |
| Deserialization lacks shared options. | Valid. Dispatch may deserialize differently from enqueue or editing paths. | Add shared serializer options or serializer abstraction. |
| Bus deserialization can become a throughput bottleneck. | Valid at scale. Runtime `JsonSerializer.Deserialize(payload, Type)` is flexible but not the fastest path. | Add serializer abstraction first; consider `JsonTypeInfo` caching or source generation after compatibility contract is stable. |
| `TargetInvocationException` unwrap is unnecessary with expression-tree calls. | Likely valid. The dispatcher no longer uses reflection invoke for handler calls. | Add an exception propagation test, then remove the legacy catch if behavior is unchanged. |
| Middleware input differs between Bus and Pipe. | Valid enhancement. The current interface documents it, but reusable middleware must branch on runtime type. | Consider a stable `JobExecutionEnvelope`. |
| Full ASP.NET/MediatR-style pipeline is missing. | Partially valid and partly outdated. A handler-level middleware pipeline already exists in Bus and Pipe dispatch. What is missing is a first-class lifecycle pipeline around fetch, lease, circuit decisions, retry scheduling, archive, and DLQ transitions. | Keep as middle priority; document the current scope now and consider lifecycle hooks after core correctness work. |
| Fluent `.UseLogging().UseRetryPolicy().UseOpenTelemetry()` style API is missing. | Valid API ergonomics request, not a preview blocker. Current API uses global `AddMiddleware<TMiddleware>()`. | Track fluent/per-profile middleware registration as future public API polish. |
| Retry policy should be middleware. | Risky as stated. Retry is a processor lifecycle concern, not just a handler wrapper. Naive retry middleware can corrupt attempt accounting and DLQ policy. | Expose retry customization through a dedicated `IRetryStrategy` or processor policy, not arbitrary handler middleware. |
| Pipe handler resolution should use `GetRequiredService`. | Low-risk cleanup. Current code throws a clear custom exception when missing. | Optional consistency improvement. |
| Pipe dispatch should log job type. | Valid low-risk diagnostic improvement. | Add debug log without logging full payload by default. |
| Pipe mode loses compile-time payload safety. | Valid by design. Pipe mode is an escape hatch for raw or dynamic payloads. | Add optional `IPipeDeserializer` or validation hook if structured Pipe jobs become common. |
| Registry allows one CLR type to map to multiple keys with first reverse key winning. | Valid but intentional. Code comment exists; public contract should document it. | Add docs and tests for reverse lookup behavior. |
| One CLR type mapped to multiple keys should be rejected. | Policy choice. Strict mode is safer, while multi-key mapping can support migrations. | Decide between strict one-key-per-type and explicit multi-key/list semantics. |
| Job versioning and schema evolution are not enforced. | Valid. Current system relies on conventions such as semantic keys. | Document and test versioned type-key patterns. |
| Per-job scope may be expensive. | Possible future performance concern. Correctness favors scoped isolation today. | Add benchmark/metrics before changing lifetime strategy. |

## Phase A: Preserve Dispatch Baseline

Goal: keep the dispatch features that already make the system strong.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| A.1 | P0 | Done | Keep DI scope per job. | Each dispatch creates an execution scope and resolves scoped services inside it. |
| A.2 | P0 | Done | Keep registry-first Bus lookup. | Registered keys resolve without reflection scan. |
| A.3 | P0 | Done | Keep expression-tree invocation. | Handler calls use compiled delegates rather than reflection invoke. |
| A.4 | P0 | Done | Keep middleware pipeline. | Middleware wraps handler execution in registration order semantics. |
| A.5 | P1 | Done | Keep Pipe mode minimal. | Pipe mode delegates raw payloads to one handler without Bus reflection. |

## Phase B: Handler Delegate Cache

Goal: make invocation caching explicit and robust if handler shapes evolve.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| B.1 | P1 | Partial | Cache compiled handler delegates. | Current cache is keyed by closed handler service type. |
| B.2 | P2 | Open | Add cache-key regression tests. | Tests prove two job types cannot share the wrong compiled delegate. |
| B.3 | P2 | Open | Consider `(handlerType, jobType)` cache key. | If added, behavior remains identical for current handlers and clearer for future handler models. |
| B.4 | P2 | Open | Validate handler method shape. | Dispatcher fails clearly if a future handler model introduces ambiguous `HandleAsync` methods. |
| B.5 | P2 | Open | Remove legacy reflection exception unwrap. | Handler exceptions propagate correctly without `TargetInvocationException` special handling. |
| B.6 | P2 | Open | Prefer signature-based handler lookup. | Delegate compilation locates `HandleAsync(TJob, CancellationToken)` or maps the closed interface method explicitly. |

Current assessment:

The current key is not an urgent bug because `handlerType` is
`IChokaQJobHandler<TJob>`, and the closed generic argument is the job type. The
pair key is mainly future-proofing.

## Phase C: Strict Type-Key Dispatch

Goal: make registry lookup the production source of truth.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| C.1 | P1 | Partial | Resolve registry before fallback. | Bus dispatch tries `JobTypeRegistry` before `Type.GetType`. |
| C.2 | P1 | Open | Add strict registry mode. | Production hosts can reject unknown job type keys instead of using CLR fallback. |
| C.3 | P1 | Open | Remove `Type.GetType` from default production path. | Unknown Bus type keys fail with a clear registration error. |
| C.4 | P1 | Open | Improve unknown-type diagnostics. | Error tells the operator which key is missing and how to register or migrate it. |
| C.5 | P2 | Open | Add trimming/AOT guidance. | Docs explain why registry keys are safer than runtime CLR type lookup. |

This phase links to:

- `private_docs/high/type-resolution-hardening-roadmap.md`

## Phase D: Serialization Contract

Goal: make dispatch deserialization match enqueue serialization.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| D.1 | P1 | Open | Add shared `JsonSerializerOptions`. | Bus dispatch deserializes with the same options used by enqueue and editing paths. |
| D.2 | P1 | Open | Add serializer abstraction. | Hosts can replace serializer behavior without changing dispatcher code. |
| D.3 | P1 | Open | Add payload compatibility tests. | Tests cover casing, converters, enums, unknown fields, nullable changes, and older payloads. |
| D.4 | P1 | Open | Add deserialization error taxonomy. | Payload failures can be classified as fatal schema/payload failures instead of generic exceptions. |
| D.5 | P2 | Open | Evaluate `JsonTypeInfo` or source generation. | High-throughput hosts can avoid repeated metadata resolution for registered job types. |
| D.6 | P2 | Open | Add schema evolution docs. | Docs recommend additive changes and new type keys for incompatible payload changes. |

Candidate contract:

```csharp
public interface IChokaQJobSerializer
{
    string Serialize(object job, Type jobType);
    object Deserialize(string payload, Type jobType);
}
```

## Phase E: Middleware Contract

Goal: make middleware reusable across Bus and Pipe modes.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| E.1 | P1 | Done | Provide middleware hook in both modes. | Bus and Pipe dispatch both call `IChokaQMiddleware`. |
| E.2 | P1 | Partial | Document mode-specific payload type. | Interface comment states Bus gets a DTO and Pipe gets raw payload string. |
| E.3 | P2 | Open | Consider `JobExecutionEnvelope`. | Middleware can access `JobId`, `JobType`, queue, raw payload, deserialized job, and mode through one stable object. |
| E.4 | P2 | Open | Add middleware portability tests. | Middleware can run correctly in Bus and Pipe modes without unsafe casts. |
| E.5 | P2 | Open | Add middleware ordering docs. | Public docs define registration order and execution order. |
| E.6 | P1 | Open | Document middleware scope. | Public docs state that middleware wraps handler dispatch and does not replace processor lifecycle policies. |
| E.7 | P2 | Open | Add middleware registration ergonomics. | Consider fluent registration helpers while preserving the existing `AddMiddleware<TMiddleware>()` API. |
| E.8 | P2 | Open | Add per-job or per-profile middleware policy. | Hosts can apply middleware selectively without runtime type checks inside each middleware. |
| E.9 | P2 | Open | Add built-in middleware examples. | Provide safe examples for logging, audit tagging, metrics timing, and idempotency without logging full payloads by default. |
| E.10 | P2 | Deferred | Consider full lifecycle hooks. | Fetch, lease, circuit breaker, retry, archive, and DLQ decisions become observable/extensible only through explicit processor-level hooks. |

Possible envelope:

```csharp
public sealed record JobExecutionEnvelope(
    string JobId,
    string JobType,
    string Queue,
    string RawPayload,
    object? DeserializedJob,
    JobDispatchMode Mode);
```

## Phase F: Pipe Mode Diagnostics

Goal: keep Pipe mode minimal while improving failure clarity.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| F.1 | P1 | Done | Use one global pipe handler. | Pipe mode dispatches every payload to `IChokaQPipeHandler`. |
| F.2 | P2 | Partial | Fail clearly when handler is missing. | Current code throws a clear custom `InvalidOperationException`. |
| F.3 | P2 | Open | Consider `GetRequiredService`. | Missing handler resolution aligns with other DI failures while preserving a useful message. |
| F.4 | P2 | Open | Add Pipe mode registration test. | AddChokaQ Pipe mode fails clearly if no handler type is configured. |
| F.5 | P2 | Open | Add debug dispatch logging. | Pipe mode logs job type and job ID at debug level without logging full payload by default. |
| F.6 | P2 | Open | Consider optional `IPipeDeserializer`. | Pipe hosts can validate or deserialize structured payloads before the global handler runs. |

Candidate optional hook:

```csharp
public interface IPipeDeserializer
{
    ValueTask<object?> DeserializeAsync(
        string jobType,
        string payload,
        CancellationToken ct);
}
```

## Phase G: Registry Contract

Goal: document how keys and types behave across versions.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| G.1 | P1 | Done | Reject duplicate keys. | Registering the same key twice throws. |
| G.2 | P2 | Partial | Allow multiple keys for one CLR type. | Reverse lookup keeps the first key, but only code comments explain this. |
| G.3 | P2 | Open | Document reverse lookup behavior. | Docs state that one type should normally have one active key, and first key wins when multiple are registered. |
| G.4 | P2 | Open | Add multi-key tests. | Tests prove first reverse key wins and duplicate keys still fail. |
| G.5 | P2 | Open | Add versioned key examples. | Examples use keys such as `email.send.v1` and `email.send.v2`. |
| G.6 | P2 | Open | Decide strict duplicate-type policy. | Registry either rejects multiple keys for one CLR type or exposes all keys explicitly for migration scenarios. |

## Phase H: Scope And Throughput Measurement

Goal: optimize only after real dispatch overhead is visible.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| H.1 | P1 | Done | Use safe scoped resolution. | Handler dependencies can be scoped per job. |
| H.2 | P2 | Open | Add dispatch benchmark. | Benchmark measures scope creation, deserialization, middleware, and delegate invocation separately. |
| H.3 | P2 | Open | Add dispatch metrics. | Runtime metrics expose dispatch duration and handler resolution failures. |
| H.4 | P2 | Deferred | Optimize scope strategy. | Only consider pooling or alternate lifetime strategy after benchmarks show scope cost dominates. |

## Phase I: Tests

Goal: prove dispatch contracts and edge cases.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| I.1 | P1 | Open | Add strict registry tests. | Unknown Bus type key fails clearly when strict mode is enabled. |
| I.2 | P1 | Open | Add serializer options tests. | Dispatcher honors shared options and converters. |
| I.3 | P1 | Open | Add payload schema failure tests. | Invalid payload moves through the intended fatal/error taxonomy. |
| I.4 | P2 | Open | Add delegate cache tests. | Cache cannot return a delegate for the wrong job type. |
| I.5 | P2 | Open | Add handler method lookup tests. | Overload or explicit-interface scenarios fail clearly or compile the intended method. |
| I.6 | P2 | Open | Add middleware envelope tests. | If envelope is introduced, Bus and Pipe middleware see consistent metadata. |
| I.7 | P2 | Open | Add registry multi-key tests. | Reverse lookup behavior is explicit and stable. |
| I.8 | P2 | Open | Add dispatch benchmark tests. | Performance baseline detects accidental reflection-invoke regressions. |

## Suggested Implementation Order

1. Add shared serializer options or serializer abstraction.
2. Add strict registry mode and unknown-type diagnostics.
3. Add serializer and strict-registry tests.
4. Document type-key versioning and registry reverse lookup behavior.
5. Add delegate cache regression tests.
6. Remove legacy exception unwrapping if tests confirm it is unnecessary.
7. Add low-risk Pipe diagnostics.
8. Document the current handler-level middleware scope before presenting it as a
   full pipeline feature.
9. Decide whether middleware needs a stable execution envelope.
10. Add dispatch benchmarks before optimizing DI scopes.

## Non-Goals

- Do not replace expression-tree dispatch with reflection invoke.
- Do not optimize away per-job DI scopes without measurement.
- Do not make CLR type names the recommended persisted job contract.
- Do not force Bus and Pipe payloads into the same shape unless middleware
  portability becomes a real requirement.
- Do not move core retry, DLQ, archive, or ownership decisions into arbitrary
  user middleware without a separate processor-level contract.
