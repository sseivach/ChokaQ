# Circuit Breakers

![Circuit breaker state flow](/diagrams/31-circuit-breaker-state-flow.png)

![The Deck circuit breakers panel](/screenshots/the-deck/circuit-breakers.png)

Circuit breakers protect the system from repeating a known-bad action.

Retry answers: "Should this individual job try again later?"  
Circuit breaker answers: "Should this job type execute at all right now?"

## States

| State | Meaning |
|---|---|
| Closed | Execution is allowed. Failures are counted inside a policy window. |
| Open | Execution is blocked. Jobs are rescheduled instead of calling the handler. |
| Half-open | A limited probe is allowed to test recovery. |

## Execution Flow

1. The processor receives a fetched job.
2. It asks the circuit breaker for an execution permit by job type.
3. If permitted, execution continues to `MarkAsProcessing`.
4. Success reports recovery to the breaker.
5. Failure reports severity to the breaker.
6. If the circuit opens, later jobs are delayed with `CircuitBreakerDelay`.

The circuit check happens before user code to avoid hammering a failing
downstream dependency.

## What The Deck Should Show

The Deck should make circuit state visible:

- circuit key;
- current state;
- failure count;
- last failure time;
- half-open probe status;
- next retry/probe time;
- operator action history.

## Operator Guidance

Do not force-close a breaker just because the queue is growing. First verify
that the downstream dependency, credentials, DNS, schema, or handler bug is
fixed. Closing a breaker against a still-broken dependency converts protection
back into a retry storm.

## Architecture Decision

### Why this pattern?

Retries protect individual jobs from temporary failures. Circuit breakers
protect the whole system from systemic failures.

### Trade-offs

A circuit breaker can delay valid work if it opens too aggressively. If it is
too permissive, it does not protect downstream systems. Policy tuning must be
based on failure rate, failure severity, and recovery behavior.

### Alternatives considered

| Alternative | Benefit | Cost |
|---|---|---|
| Retry only | Simpler. | Can overwhelm a broken dependency. |
| Global pause | Strong protection. | Stops unrelated job types. |
| Downstream rate limit only | Delegates protection. | Workers can still spend capacity on calls that the dependency is already rejecting. |

### Interview questions

**Why not just use retries?**  
Retries react per job. A circuit breaker reacts to systemic failure and stops
new executions before they hit the broken dependency.

**What is half-open for?**  
It allows a controlled recovery probe without unleashing the full backlog.

**What should happen when the breaker is open?**  
Jobs should be rescheduled or held, not executed and failed repeatedly.
