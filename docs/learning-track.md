# Architecture Learning Track

ChokaQ is both a background job processor and a practical architecture study
project. The product goal is useful software. The learning goal is a codebase
that explains how production patterns work when they are forced to survive real
state, failures, operators, dashboards, and tests.

Use this page as the reading order for senior-to-architect growth, interview
preparation, and future book-style documentation.

## How To Read ChokaQ

Read each feature in four passes:

1. Product behavior: what the user or operator can do.
2. Runtime contract: what guarantee the system claims.
3. Failure model: what can still go wrong and how the code responds.
4. Test evidence: which tests pin the behavior so future changes cannot quietly
   weaken it.

This is the difference between "we added a queue" and "we understand a queue."
The first is a feature. The second is architecture.

## Chapter Template

Each deep-dive chapter should eventually follow the same structure:

| Section | Purpose |
|---|---|
| Problem | The real production pressure that makes the pattern necessary. |
| Naive Approach | The simple implementation most teams try first. |
| Why It Fails | Race conditions, scale limits, operational blind spots, or bad user experience. |
| Production Pattern | The general architecture idea, independent of ChokaQ. |
| ChokaQ Implementation | The exact classes, SQL, options, and dashboard flows used here. |
| Code Walkthrough | File-by-file explanation of the important path. |
| Tests And Guarantees | Unit/integration tests that prove the contract. |
| Operational Signals | Metrics, health checks, logs, and dashboard views operators should watch. |
| Interview Notes | How to explain the design tradeoffs in a system-design conversation. |
| Future Work | Known gaps, rejected alternatives, and what should change at larger scale. |

## Study Map

| Topic | Why It Matters | Current Entry Point |
|---|---|---|
| Three Pillars | Separates active work, success history, and failed jobs so each table has a focused workload. | [Three Pillars](/1-architecture/three-pillars) |
| SQL Competing Consumers | Shows how workers claim jobs without a distributed lock service. | [SQL Concurrency](/3-deep-dives/sql-concurrency) |
| State Machine | Makes every lifecycle transition explicit and testable. | [State Machine](/2-lifecycle/state-machine) |
| Worker Ownership And Leases | Prevents stale workers from finalizing work they no longer own. | [State Machine](/2-lifecycle/state-machine) |
| Backpressure | Explains what happens when producers are faster than consumers. | [Backpressure Policy](/3-deep-dives/backpressure-policy) |
| Bulkhead Isolation | Keeps one queue from consuming all worker capacity. | [Bulkhead Isolation](/2-lifecycle/bulkhead-isolation) |
| Circuit Breaker | Stops repeated dependency failures from turning into system-wide damage. | [Smart Worker](/1-architecture/smart-worker) |
| Retries And Jitter | Turns transient failure into delayed work without creating retry storms. | [Runtime Configuration](/configuration) |
| Zombie Recovery | Handles process crashes, abandoned fetches, and expired processing heartbeats. | [Zombie Rescue](/2-lifecycle/zombie-rescue) |
| Idempotency | Defines what duplicate protection can and cannot promise. | [Getting Started](/getting-started) |
| Observability | Connects metrics, health checks, structured logs, and dashboard state. | [Rolling Observability](/4-the-deck/rolling-observability) |
| Admin Safety | Treats dashboards as production control planes, not just pretty UI. | [Getting Started](/getting-started) |
| Release Readiness | Separates code that works locally from software that can be adopted safely. | [Release Strategy](/release-strategy) |

## Evolutionary Lessons

For the future architecture-book version of ChokaQ, key features should be
documented as design evolution, not only as final polished code:

1. Show the first simple implementation.
2. Explain why it was reasonable at that stage.
3. Name the operational or correctness weakness.
4. Introduce the stronger production version.
5. Compare the trade-offs directly.

The rolling observability upgrade is the template: ChokaQ first used bounded
Archive/DLQ lookbacks for throughput and failure rate, then moved to
transactional `MetricBuckets` once the limits were clear.

## Interview Framing

When explaining ChokaQ in an interview, start with the contracts:

- At-least-once execution means handlers must tolerate duplicates.
- SQL Server is the coordination boundary for durable mode.
- `UPDLOCK + READPAST` claims work, but leases and ownership guards protect the
  rest of the lifecycle.
- DLQ is not just storage for failures; it is an operator workflow.
- Observability is part of the product contract because background systems fail
  while nobody is looking at the request path.
- Configuration belongs in the host application because operators need to tune
  timeouts, retries, saturation thresholds, and cardinality budgets per
  environment.

## Writing Standard

New feature documentation should explain both "what" and "why." It is acceptable
for docs and comments to repeat important ideas. Repetition is useful when the
same concept appears from different angles: user setup, code walkthrough,
operational runbook, and system-design explanation.

The target reader is technical and capable. The docs should not talk down to the
reader, but they should still be explicit about tradeoffs, edge cases, and
failure modes. Enterprise systems are made of boring details that somebody
remembered to write down.
