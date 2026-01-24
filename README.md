# 🍫 ChokaQ

![.NET 10](https://img.shields.io/badge/.NET-10.0%20-blue)
![License](https://img.shields.io/badge/license-MIT-green)
![Status](https://img.shields.io/badge/status-Active%20Development-orange)

**Current Status:** Work in Progress / Proof of Concept

**ChokaQ** is a lightweight, high-performance background job engine designed for .NET 10.
It bridges the gap between simple in-memory `Channel<T>` implementations and complex Enterprise Service Bus solutions.

**Zero Dependencies Policy:**
* The **Core** engine is strictly dependency-free.
* The optional **SQL Storage** provider utilizes **Dapper** (micro-ORM) for maximum performance and efficiency.

---

## 🧠 Architecture: The 2x2 Matrix

ChokaQ is built upon a modular architecture. You select the **Processing Strategy** (The Brain) and the **Storage Strategy** (The Memory).
While you may mix these as required, we have curated two reference modes for optimal performance.

| | **In-Memory Storage** (Volatile) | **SQL Server Storage** (Persistent) |
| :--- | :--- | :--- |
| **Pipe Strategy**<br>*(Raw JSON / Events)* | 🚀 **Rocket Mode**<br>Logs, metrics, fire-and-forget.<br>Maximum throughput; data resides in RAM. | **Integration Mode**<br>Collection of raw events into an external database for analytics. |
| **Bus Strategy**<br>*(Typed Jobs / Profiles)* | **Dev Sandbox**<br>Development of business logic without database infrastructure. | 🚌 **Enterprise Mode**<br>Business transactions, reports, notifications.<br>High reliability: retries, history, and resilience to restarts. |

---

## 🚀 Quick Start (Samples)

The `/samples` directory contains two complete projects demonstrating best practices.

### 1. Rocket Mode (`ChokaQ.Sample.Pipe`)
*Ideal for: Logs, metrics, and notifications where minor data loss is acceptable.*

* **Type:** Pipe (Raw Payloads)
* **Storage:** RAM (System.Threading.Channels)
* **Feature:** Zero database dependency. Instant startup and execution.

**How to Run:**
1.  Open `ChokaQ.sln`.
2.  Set `ChokaQ.Sample.Pipe` as the startup project.
3.  Press **F5**.
4.  The launcher will open in your browser. Click "Fire Into Pipe".
5.  The Dashboard is available via the button or at `/chokaq`.

---

### 2. Enterprise Mode (`ChokaQ.Sample.Bus`)
*Ideal for: Critical workloads, billing, and report generation.*

* **Type:** Bus (Typed DTOs + Profiles)
* **Storage:** SQL Server (Dapper + Polling)
* **Feature:** Complete reliability. Data is persisted in SQL. The database schema is automatically managed.

**How to Run:**
1.  Ensure a SQL Server instance is available (LocalDB or Docker).
2.  Create an **empty** database named `ChokaQDb`:
    ```sql
    CREATE DATABASE [ChokaQDb];
    ```
3.  Verify the connection string in the `appsettings.json` file of the `ChokaQ.Sample.Bus` project.
4.  Press **F5**.
    * The application will automatically create the `chokaq` schema, tables, and indexes.
    * Workers will commence polling.
5.  Generate jobs via the UI, stop the application, and restart it to verify that pending jobs resume execution.

---

## ⚙️ Configuration in Code

### Option A: Rocket Mode (Pipe + Memory)
In `Program.cs`:
```csharp
// 1. Register Pipe with the Global Handler
builder.Services.AddChokaQ(options => 
{
    options.UsePipe<GlobalPipeHandler>();
    // Memory is configured by default, but limits can be adjusted
    options.ConfigureInMemory(mem => mem.MaxCapacity = 50_000); 
});

// 2. Add Dashboard
builder.Services.AddChokaQDashboard();
```

### Option B: Enterprise Mode (Bus + SQL)
In `Program.cs`:
```csharp
// 1. Register Bus and Profiles
builder.Services.AddChokaQ(options =>
{
    options.AddProfile<MailingProfile>();
    options.AddProfile<ReportingProfile>();
});

// 2. Configure SQL Storage
builder.Services.UseSqlServer(options =>
{
    options.ConnectionString = "...";
    options.SchemaName = "chokaq"; // Isolate tables in a separate schema
    options.AutoCreateSqlTable = true; // Auto-migrations on startup
});

// 3. Add Dashboard
builder.Services.AddChokaQDashboard();
```

---

## 📊 Dashboard

The Dashboard functions out-of-the-box in any mode.
* **Real-time:** Updates via SignalR.
* **Search:** Filtering by ID, type, and content.
* **Control:** Jobs can be retried or canceled directly.
* **Stats:** Accurate statistics across the entire database (utilizing optimized SQL aggregations or in-memory counters).

Mapping:
```csharp
app.MapChokaQDashboard(); // Default path: /chokaq
```

---

## 🏗️ Project Structure

* `src/ChokaQ.Abstractions` — Contracts, DTOs, Interfaces.
* `src/ChokaQ.Core` — Dispatch logic, In-Memory provider, Workers.
* `src/ChokaQ.Storage.SqlServer` — Persistence implementation using Dapper.
* `src/ChokaQ.Dashboard` — Blazor UI (RCL).

## Author

Created by **Sergei Seivach**.

## License

MIT.