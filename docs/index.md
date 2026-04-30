---
layout: home

hero:
  name: "ChokaQ"
  text: "Zero-Dependency Job Engine for .NET 10"
  tagline: "Atomic reliability · Real-time observability · No bloated ORMs · No third-party magic"
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
    details: Physical data separation into Hot, Archive, and DLQ tables. Zero index fragmentation. Consistent query performance regardless of historical data volume.
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
    details: 50 workers fetching from one table simultaneously — zero deadlocks, zero duplicate processing. Industry-standard competing consumer pattern.
    link: /3-deep-dives/sql-concurrency
  - icon: 🛡️
    title: Bulkhead Isolation
    details: Per-queue MaxWorkers enforced at database level. Heavy PDF generation will never starve lightweight SMS notifications.
    link: /2-lifecycle/bulkhead-isolation
  - icon: 🔧
    title: Zero Dependencies
    details: "No EF Core, no Dapper, no Polly, no third-party libraries. Custom micro-ORM (SqlMapper), custom Circuit Breaker, custom retry policy. Only Microsoft.Data.SqlClient."
    link: /1-architecture/zero-dependency
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
| **3a. Success** | Job completes → atomically moved to **JobsArchive** | `INSERT...SELECT + DELETE` in single transaction |
| **3b. Failure** | Fatal error or max retries → moved to **JobsDLQ** | Smart Worker classification, Circuit Breaker |
| **3c. Retry** | Transient error → stays in Hot with delayed visibility | Exponential backoff + jitter |

<br>

::: tip 💡 Self-Healing
The **ZombieRescueService** runs every 60 seconds, scanning for jobs stuck in `Processing` with expired heartbeats. Crashed workers? No problem — zombies are automatically recovered or archived.
:::

<br>

> *Ready to dive in? Start with the [Three Pillars Architecture](/1-architecture/three-pillars) or see how a job travels through the system in [The State Machine](/2-lifecycle/state-machine).*