---
layout: home

hero:
  name: "ChokaQ"
  text: ".NET 10 Background Job Engine"
  tagline: "A SQL-backed background job engine with typed jobs, retries, queue isolation, health checks, and The Deck dashboard."
  actions:
    - theme: brand
      text: Start Here
      link: /overview
    - theme: alt
      text: Try ChokaQ
      link: /try-chokaq
  image:
    src: /logo.png
    alt: ChokaQ Logo

features:
  - icon: storage
    title: Durable SQL Storage
    details: Accepted work is stored in JobsHot. Finished work moves to Archive, and failed work moves to DLQ for operator review.
    link: /1-architecture/three-pillars
  - icon: code
    title: Typed Jobs
    details: Define a DTO, write a handler, register a type key, and enqueue work through IChokaQQueue.
    link: /job-contracts
  - icon: shield
    title: At-Least-Once Execution
    details: ChokaQ can preserve and retry work, but handlers that touch external systems still need idempotency.
    link: /delivery-guarantees
  - icon: dashboard
    title: The Deck
    details: A real-time dashboard for active jobs, queues, DLQ, health, worker controls, and recovery workflows.
    link: /4-the-deck/realtime-signalr
  - icon: settings
    title: Runtime Policy
    details: Timeouts, retries, queue limits, health thresholds, SQL settings, and metrics caps live in configuration.
    link: /configuration
  - icon: book
    title: Detailed Documentation
    details: The docs explain setup, runtime behavior, delivery guarantees, operator workflows, and the architecture behind durable processing.
    link: /overview
---

## What ChokaQ Is

ChokaQ is a background job engine for .NET applications.

In normal request/response code, the user waits while your app does everything.
That is fine for quick work. It is not fine for slow or unreliable work like
sending email, rendering reports, calling partner APIs, charging payments, or
processing webhooks.

ChokaQ lets the request say: "store this work, run it in the background, and
make its state visible."

The core idea is simple:

1. Your application enqueues a job.
2. ChokaQ stores the job.
3. A worker claims the job.
4. Your handler runs.
5. ChokaQ records the final state.
6. Operators can inspect what happened in The Deck.

## The Mental Model

Think about ChokaQ as three cooperating parts:

| Part | Plain explanation | Why it matters |
|---|---|---|
| Job storage | The durable notebook of accepted work. | Jobs can survive process restarts in SQL Server mode. |
| Workers | The background runners that claim and execute jobs. | Work happens outside the request path. |
| The Deck | The operator window into the system. | People can see lag, failures, queues, retries, and DLQ rows. |

The most important table is `JobsHot`. It contains active work: jobs waiting to
run, jobs fetched by a worker, and jobs currently processing. Finished jobs move
out of Hot so the active-work table stays small.

## What To Read First

If you are new to ChokaQ, start with [Runtime Model](/runtime-model). It explains
where ChokaQ runs, how it stores work, and how your handlers are called. Then
continue with [Overview](/overview) for the broader tour.

If you want to run code immediately, start with [Try ChokaQ](/try-chokaq).
It gives you a SQL-backed sample, The Deck, health checks, and scenario buttons
in one short path.

If you are evaluating reliability, read [Delivery Guarantees](/delivery-guarantees)
before writing a handler that sends email, charges money, or calls another
system.

If you want to understand the architecture, read
[Three Pillars](/1-architecture/three-pillars), [State Machine](/2-lifecycle/state-machine),
and [SQL Concurrency](/3-deep-dives/sql-concurrency).

## Data Flow

| Step | What happens | Human meaning |
|---|---|---|
| Enqueue | A job is inserted into `JobsHot`. | The system accepted the work. |
| Fetch | A worker claims eligible work with SQL locking. | One worker owns the job now. |
| Processing | The handler runs user code. | Your business operation is happening. |
| Success | The row moves to Archive. | The job finished normally. |
| Retry | The row stays in Hot with a future schedule. | Try again later without sleeping a thread. |
| DLQ | The row moves to Dead Letter Queue. | A human or repair workflow should inspect it. |

::: tip Start Small
Run the sample first, then read the docs while looking at The Deck. The UI makes
the architecture easier to understand because you can see jobs move between
states instead of only reading about them.
:::
