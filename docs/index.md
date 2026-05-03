---
layout: home

hero:
  name: "ChokaQ"
  text: ".NET 10 Background Job Engine"
  tagline: "SQL-backed reliability, real-time operations, and architecture lessons on production patterns"
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
    title: Expression Trees (Zero Reflection)
    details: Handlers invoked via cached compiled delegates — near-native execution speed. No MethodInfo.Invoke, no reflection overhead, no Activator.CreateInstance.
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
    details: Per-queue MaxWorkers enforced at database level. Heavy PDF generation will never starve lightweight SMS notifications.
    link: /2-lifecycle/bulkhead-isolation
  - icon: 🔧
    title: Minimal Dependencies
    details: "No EF Core, no Dapper, no Polly. ChokaQ keeps infrastructure code explicit and relies on the official Microsoft SQL client for SQL Server access."
    link: /1-architecture/minimal-dependencies
  - icon: 📚
    title: Architecture Learning Track
    details: The docs are growing into a practical architecture guide that explains backpressure, circuit breakers, bulkheads, retries, leases, and observability using this codebase.
    link: /learning-track
---

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

> *Ready to dive in? Start with the [Three Pillars Architecture](/1-architecture/three-pillars), run the [Docker Compose Sample](/samples/docker-compose), or use the [Learning Track](/learning-track) as a study map.*
