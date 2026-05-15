---
layout: home

hero:
  name: "ChokaQ"
  text: ".NET 10 Background Job Engine"
  tagline: "A production-preview .NET 10 job engine for SQL-backed reliability, real-time operations, and explicit concurrency control without the infrastructure weight of external brokers."
  actions:
    - theme: brand
      text: Get Started →
      link: /getting-started
    - theme: alt
      text: Why ChokaQ?
      link: /why-chokaq
  image:
    src: /logo.png
    alt: ChokaQ Logo

features:
  - icon: 🏛️
    title: Three Pillars Architecture
    details: Physical data separation into Hot, Archive, and DLQ tables. Active-work indexes stay small while history and failed jobs get their own read paths.
    link: /1-architecture/three-pillars
  - icon: ⚡
    title: Compiled Handler Dispatch
    details: Handlers are invoked through cached compiled delegates, which keeps dispatch overhead low without asking user code to manage reflection plumbing.
    link: /3-deep-dives/expression-trees
  - icon: 🧠
    title: Smart Worker (Fast-Fail)
    details: "Fatal exceptions (NullRef, ArgumentEx) bypass retry policies and go straight to the DLQ. No more burning resources on code bugs."
    link: /1-architecture/smart-worker
  - icon: 🔒
    title: SQL Concurrency (UPDLOCK + READPAST)
    details: Competing consumers claim work with SQL locking hints and ownership guards, reducing lock contention and preventing duplicate claims.
    link: /3-deep-dives/sql-concurrency
  - icon: 🛡️
    title: Bulkhead Isolation
    details: Per-queue MaxWorkers enforced at database level. Heavy workloads can be isolated from lightweight queues so one workload does not consume all execution capacity.
    link: /2-lifecycle/bulkhead-isolation
  - icon: 🔧
    title: Minimal Dependencies
    details: "No EF Core, no Dapper, no Polly. ChokaQ keeps infrastructure code explicit and relies on the official Microsoft SQL client for SQL Server access."
    link: /1-architecture/minimal-dependencies
  - icon: 📚
    title: Architecture Study Guide
    details: The docs are growing into a practical architecture guide that explains backpressure, circuit breakers, bulkheads, retries, leases, and observability using this codebase.
    link: /study-guide
  - icon: docs
    title: Operations Runbooks
    details: SLOs, alerts, queue-lag triage, DLQ recovery guidance, worker-health checks, and safe bulk-recovery procedures for people operating the system.
    link: /5-operations/runbooks
---

<br>

## Delivery Guarantees

ChokaQ provides at-least-once execution. It does not provide exactly-once
external side effects, and in-memory mode is process-local rather than durable.
Read [Delivery Guarantees](/delivery-guarantees) before using ChokaQ for
side-effecting jobs.

<br>

## The Architecture at a Glance

<img src="/architecture.png" alt="ChokaQ Three Pillars Architecture" style="width: 100%; max-width: 960px; margin: 0 auto; display: block;" />

<br>

### How Data Flows Through the System

| Step | What Happens | Key Technology |
|------|-------------|---------------|
| **1. Enqueue** | API submits a job → inserted into **JobsHot** table | Idempotency keys, priority, delayed scheduling |
| **2. Fetch** | Worker atomically locks a batch of pending jobs | `UPDLOCK, READPAST` (SQL) or `Channel<T>` (RAM) |
| **3a. Success** | Job completes → atomically moved to **JobsArchive** | Transactional state transition with ownership guards |
| **3b. Failure** | Fatal error or max retries → moved to **JobsDLQ** | Smart Worker classification, Circuit Breaker |
| **3c. Retry** | Transient error → stays in Hot with delayed visibility | Exponential backoff + jitter |

<br>

::: tip 💡 Self-Healing
The **ZombieRescueService** periodically scans for jobs stuck in `Fetched` or `Processing` with expired leases or heartbeats. Abandoned fetched jobs can return to Pending; processing zombies move to DLQ for operator review.
:::

<br>

> *Ready to dive in? Start with the [Three Pillars Architecture](/1-architecture/three-pillars), run the [Docker Compose Sample](/samples/docker-compose), validate the [Local NuGet Lab](/samples/nuget-lab), or use the [Operations Runbooks](/5-operations/runbooks) when you want to understand how ChokaQ behaves under pressure.*
