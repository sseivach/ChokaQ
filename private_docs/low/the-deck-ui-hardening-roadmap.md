# The Deck UI Hardening Roadmap

This file tracks performance, state-management, and operator UX hardening work
for The Deck Blazor UI. It is intentionally private and not linked from the
public documentation navigation.

This roadmap is low priority for the first NuGet preview. The issues here are
not core queue correctness risks, but The Deck is an operator control plane.
Rendering stalls, confusing selection state, or unclear panel transitions can
slow down incident response in later production-oriented releases.

## Current State

The Deck already has a solid component boundary:

- `JobMatrix` is mostly a presentation component.
- `OpsPanel` acts as the side-panel orchestrator.
- `Stats` is a simple display component.
- Actions flow up through `EventCallback`.
- `JobMatrix` already uses Blazor `Virtualize`.
- Bulk actions require an arming/confirmation step.
- Selected IDs are copied before invoking bulk callbacks.
- Top-level page updates from SignalR use throttling for stats refreshes.
- The top-level Deck page also performs unconditional storage reconciliation
  every 2 seconds.
- Queue management currently refreshes on a separate 1-second timer.
- Circuit countdown rendering uses a local timer while any circuit is open.

Remaining gaps:

- The dashboard still has always-on polling even though SignalR events already
  carry job and stats updates.
- The public docs should not claim "push-based, not polling" while this fixed
  reconciliation timer exists.
- Timer-driven `LoadDataAsync` can overlap itself if a storage read takes
  longer than the timer interval.
- Polling cadence is not configurable from `ChokaQTheDeckOptions`.
- Polling does not back off when the page is idle, the hub is healthy, or no
  recent state changes have happened.
- Queue management uses a separate polling loop instead of sharing the page
  refresh model or consuming queue-change events.
- `FilteredJobs` is a computed property that allocates a new list on every
  access.
- `FilteredJobs` is accessed multiple times in the same render.
- Search uses `oninput` without debounce.
- Selection callbacks expose mutable `HashSet<string>` instead of immutable
  snapshots.
- Select-all mutates the existing `HashSet` item by item.
- `OpsPanel` state is spread across several fields instead of one explicit
  state object.
- `OnParametersSet` performs implicit tab transitions.
- Some `StateHasChanged` calls may be redundant in normal component event
  paths.
- A few async methods are wrappers around synchronous state changes.

## Design Principles

- Render paths should not allocate more than necessary.
- Filtering should be recalculated only when jobs, filters, or search text
  change.
- UI selection snapshots should be immutable at component boundaries.
- Virtualization should receive stable item collections.
- Search should balance responsiveness with CPU and allocation cost.
- Operator navigation should use explicit transitions instead of implicit
  lifecycle side effects.
- Manual `StateHasChanged` should be reserved for non-Blazor event sources,
  external callbacks, or intentionally forced rerenders.
- SignalR should be the primary update path for live state.
- Polling should remain available as a reconciliation fallback because SignalR
  delivery is not durable and clients can reconnect after missing events.
- Fixed polling must be single-flight and bounded so a slow storage read cannot
  create overlapping refreshes.
- Public docs must describe the actual consistency model: push-assisted live
  updates plus periodic/fallback reconciliation.

## Status Legend

| Status | Meaning |
|---|---|
| Done | Implemented, tested, and documented for current scope. |
| Partial | Some behavior exists, but the acceptance criteria below are not complete. |
| Open | Not implemented yet. |
| Deferred | Intentionally postponed until higher-risk items are handled. |

## Executive Sequence

1. Before NuGet, correct any public docs that claim The Deck is purely
   push-based if the fixed polling timer remains.
2. Add a single-flight guard around dashboard reconciliation.
3. Convert unconditional polling into event-driven refresh with configurable
   fallback reconciliation.
4. Cache filtered jobs and derived selection state.
5. Debounce active search input.
6. Switch bulk event payloads to immutable ID snapshots.
7. Optimize select-all selection creation.
8. Audit manual `StateHasChanged` calls.
9. Make `OpsPanel` transitions explicit.
10. Add simple rendering and selection tests where practical.
11. Add large-list UI performance checks for 1k to 10k rows.

## Latest Audit Classification

| Finding | Current Status | Action |
|---|---|---|
| Dashboard polls every 2 seconds regardless of activity. | Valid. `TheDeck.razor.cs` starts a 2-second timer that calls storage reconciliation even though SignalR handlers exist. | Low-priority implementation hardening; keep polling as fallback, not the primary always-on path. |
| Polling creates unnecessary database load. | Valid. The top-level refresh reads summary, health, circuit state, and live/history slices. Queue management also polls separately every second. | Make fallback cadence configurable and back off when SignalR is healthy and no reconciliation is needed. |
| Polling creates network traffic. | Partially valid. In Blazor Server the main extra cost is server-side storage/DB work and UI circuit traffic, not browser REST polling. | Describe the cost precisely in docs and comments. |
| Event-driven updates only, polling as fallback. | Valid target. SignalR is already used for job and stats updates, but it is not durable enough to remove reconciliation entirely. | Use SignalR as primary path, run polling on reconnect, manual refresh, missed-event suspicion, mutation reconciliation, or slow fallback interval. |
| Public docs say updates are push-based, not polling. | Valid release-claim issue if the current code remains. | Before NuGet, either change the implementation or soften the docs to "push-assisted with reconciliation fallback." |
| Timer refresh can overlap. | Valid edge case. `System.Timers.Timer.Elapsed` can fire again while `LoadDataAsync` is still running. | Add a single-flight refresh gate before tuning cadence. |

## Phase A: Component Boundary Baseline

Goal: preserve the good smart/dumb component split.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| A.1 | P1 | Done | Keep `JobMatrix` presentation-focused. | Table component raises actions through callbacks instead of mutating storage. |
| A.2 | P1 | Done | Keep `OpsPanel` as orchestrator. | Inspector, editor, and history filter state stays in the side-panel coordinator. |
| A.3 | P1 | Done | Keep `Stats` simple. | Stats component remains a parameter-driven display component. |
| A.4 | P1 | Done | Use `EventCallback`. | UI actions support async parent handlers and Blazor lifecycle integration. |
| A.5 | P2 | Open | Document component responsibilities. | Private or public docs explain which component owns selection, filtering, loading, and mutation. |

## Phase B: Filtered Jobs Recalculation

Goal: remove repeated filtering and allocation from the render path.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| B.1 | P1 | Open | Cache filtered jobs. | `FilteredJobs` becomes a cached list updated only when inputs change. |
| B.2 | P1 | Open | Cache filtered count. | Search count reads cached count instead of triggering filtering. |
| B.3 | P1 | Open | Cache all-visible-selected state. | Header checkbox uses derived state from the cached filtered list. |
| B.4 | P2 | Open | Track input versions. | Jobs/filter/search changes mark cached data dirty explicitly. |
| B.5 | P2 | Open | Add render allocation check. | A large-list render does not allocate a new filtered list per binding access. |

Current expensive pattern:

```csharp
private ICollection<JobViewModel> FilteredJobs
{
    get
    {
        IEnumerable<JobViewModel> query = Jobs;
        // filters...
        return query.ToList();
    }
}
```

Target shape:

```csharp
private IReadOnlyList<JobViewModel> _filteredJobs = Array.Empty<JobViewModel>();

protected override void OnParametersSet()
{
    RebuildFilteredJobsIfNeeded();
}
```

## Phase C: Search Input

Goal: keep search responsive without filtering on every keystroke under load.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| C.1 | P1 | Partial | Support active search. | `JobMatrix` filters by ID, type, queue, and creator. |
| C.2 | P1 | Open | Add debounce. | `oninput` search waits about 250-300 ms before recalculating. |
| C.3 | P1 | Open | Split raw and applied search text. | Typing updates raw input; filtering uses debounced applied text. |
| C.4 | P2 | Open | Normalize search once. | Trim and case-insensitive term preparation happens once per search change. |
| C.5 | P2 | Open | Add search clear behavior test. | Clearing search resets filtered rows and selection header state correctly. |

Alternative:

- For small lists, `oninput` is fine.
- For 1k+ rows with SignalR churn, debounce is the safer default.
- History search already uses an explicit load action, so the main risk is live
  `JobMatrix` search.

## Phase D: Selection Model

Goal: make selection cheap and immutable at component boundaries.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| D.1 | P1 | Partial | Copy selected IDs before callback. | Current code creates a new `HashSet`, but still exposes a mutable set type. |
| D.2 | P1 | Open | Change bulk callback type. | Bulk callbacks accept `IReadOnlyCollection<string>` or `string[]`. |
| D.3 | P1 | Open | Use immutable snapshot before mutation. | Component captures selected IDs, resets local state, then invokes parent with immutable data. |
| D.4 | P1 | Open | Optimize select all. | Select-all replaces the set with a new `HashSet` built from cached filtered IDs. |
| D.5 | P2 | Open | Add selection pruning. | Selection removes IDs no longer present after source/filter/page changes. |
| D.6 | P2 | Open | Add selection tests. | Tests cover select all, clear, source switch, search change, and bulk confirm. |

Target callback shape:

```csharp
[Parameter] public EventCallback<IReadOnlyCollection<string>> OnBulkCancel { get; set; }
```

Target confirm shape:

```csharp
var selectedIds = _selectedJobIds.ToArray();
ClearSelection();
await OnBulkCancel.InvokeAsync(selectedIds);
```

## Phase E: Virtualization And Large Lists

Goal: keep The Deck usable with large job lists.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| E.1 | P1 | Done | Use Blazor `Virtualize`. | `JobMatrix` renders rows through `Virtualize` with `ItemSize` and overscan. |
| E.2 | P1 | Partial | Use stable item collection. | Virtualize receives `FilteredJobs`, but that property currently returns a new list on access. |
| E.3 | P1 | Open | Feed virtualization from cached list. | `Virtualize Items` uses `_filteredJobs`. |
| E.4 | P2 | Open | Validate row height. | `ItemSize` matches actual rendered row height closely enough to avoid scroll jumps. |
| E.5 | P2 | Open | Add large-list smoke test. | 10k jobs can scroll, search, and select without visible UI stalls. |

Audit note:

The original "no virtualization" finding is already fixed. The remaining
problem is feeding virtualization from a property that recalculates and
allocates.

## Phase F: OpsPanel State Model

Goal: make side-panel navigation predictable.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| F.1 | P1 | Partial | Centralize panel orchestration. | `OpsPanel` owns tab, selected job, and editor model state. |
| F.2 | P2 | Open | Introduce explicit panel state record. | Active tab, selected job ID, editor model, and mode are represented together. |
| F.3 | P2 | Open | Replace implicit mode transitions. | `OnParametersSet` delegates to an explicit transition method. |
| F.4 | P2 | Open | Add transition tests. | Enter history, exit history, open inspector, edit, save, close, and clear panel are deterministic. |

Candidate shape:

```csharp
private sealed record PanelState(
    OpsTab ActiveTab,
    string? SelectedJobId,
    JobEditorModel? EditorModel);
```

The state-machine refactor is useful, but not a production-preview blocker.

## Phase G: Render Lifecycle Hygiene

Goal: remove unnecessary rerenders while preserving explicit updates from
external sources.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| G.1 | P1 | Partial | Use `InvokeAsync(StateHasChanged)` for SignalR paths. | Page-level external updates already use `InvokeAsync` in several places. |
| G.2 | P2 | Open | Audit component event handlers. | Manual `StateHasChanged` calls inside normal Blazor callbacks are removed unless needed. |
| G.3 | P2 | Open | Convert async wrappers where possible. | Methods that only mutate state and return completed tasks are simplified. |
| G.4 | P2 | Open | Add comments for forced rerenders. | Remaining manual rerenders explain the external event or forced transition. |

Current examples:

- `OpsPanel.SwitchTab` calls `StateHasChanged`.
- `ClearPanel` calls `StateHasChanged`.
- Page-level SignalR callbacks correctly need explicit UI dispatch.

## Phase H: Dashboard Refresh Model

Goal: make The Deck push-first without losing eventual reconciliation.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| H.1 | P1 | Partial | Use SignalR for live updates. | Job updates and stats updates already arrive through hub callbacks. |
| H.2 | P1 | Done | Correct public docs claim. | Public docs no longer say The Deck is purely push-based while fixed polling exists. |
| H.3 | P1 | Done | Add single-flight refresh guard. | A slow storage refresh cannot overlap with the next timer tick or SignalR-triggered reconciliation. |
| H.4 | P2 | Open | Replace always-on top-level polling. | The 2-second timer becomes configurable fallback reconciliation instead of unconditional refresh. |
| H.5 | P2 | Open | Refresh after reconnect. | Hub reconnect triggers one canonical storage reconciliation. |
| H.6 | P2 | Open | Refresh after operator mutations. | Mutating commands continue to reload canonical state after the command path settles. |
| H.7 | P2 | Open | Add idle/backoff policy. | Fallback reconciliation slows down when no events or mutations have occurred recently. |
| H.8 | P2 | Open | Unify queue refresh model. | Queue panel refreshes from queue events or shared reconciliation rather than an independent 1-second polling loop. |
| H.9 | P2 | Open | Make refresh cadence configurable. | `ChokaQTheDeckOptions` exposes fallback interval and minimum throttle settings. |
| H.10 | P2 | Deferred | Durable event reconciliation. | Outbox/event sequence numbers can later tell the UI exactly when it missed events. |

Target behavior:

```text
SignalR event -> patch visible state -> throttled render
Operator mutation -> short settle delay -> canonical storage reconciliation
Hub reconnect -> canonical storage reconciliation
Fallback timer -> single-flight reconciliation at configurable/backoff cadence
```

## Phase I: Tests And Measurement

Goal: prove UI improvements with practical checks rather than guesswork.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| I.1 | P1 | Open | Add component test for filter caching. | Filtering runs once per relevant input change, not per binding access. |
| I.2 | P1 | Open | Add selection behavior tests. | Bulk actions receive immutable snapshots and local state resets predictably. |
| I.3 | P2 | Open | Add large-list manual scenario. | Document a 1k/10k row browser check for search, scroll, select all, and bulk confirm. |
| I.4 | P2 | Open | Add render allocation benchmark if feasible. | Compare current computed property vs cached list under repeated renders. |
| I.5 | P2 | Open | Add visual regression screenshots. | Main Deck layouts remain stable after virtualization/filter changes. |
| I.6 | P2 | Open | Add refresh single-flight test. | Concurrent timer and SignalR refresh requests collapse into one active storage read. |
| I.7 | P2 | Open | Add refresh fallback test. | Reconnect and fallback timer paths reconcile storage without requiring continuous 2-second polling. |

## Suggested Implementation Order

1. Correct the public refresh-model claim before NuGet if implementation stays
   polling-assisted. Done.
2. Add single-flight refresh protection. Done.
3. Convert fixed polling into configurable fallback reconciliation.
4. Cache `_filteredJobs` and derived selection state.
5. Feed `Virtualize` and search count from the cached list.
6. Add debounced search text.
7. Change bulk callback payloads to immutable ID snapshots.
8. Optimize select-all by replacing the `HashSet`.
9. Audit `StateHasChanged` calls in `OpsPanel` and page components.
10. Refactor `OpsPanel` into an explicit state record if the component keeps
   growing.
11. Add large-list checks and selection tests.

## Non-Goals

- Do not rebuild The Deck frontend architecture from scratch.
- Do not replace Blazor just to solve list rendering.
- Do not optimize UI paths before preserving operator safety checks.
- Do not remove `Virtualize`; fix the collection feeding it.
- Do not remove all reconciliation. SignalR delivery is useful, but storage
  remains the source of truth.
