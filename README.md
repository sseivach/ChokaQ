# ChokaQ

![.NET 10](https://img.shields.io/badge/.NET-10.0%20(Preview)-blue)
![License](https://img.shields.io/badge/license-MIT-green)
![Status](https://img.shields.io/badge/status-Active%20Development-orange)

**Current Status:** Work in Progress / Proof of Concept

ChokaQ is a lightweight, high-performance background job engine designed explicitly for .NET 10.

It bridges the gap between simple in-memory queues and heavy enterprise service buses. Built with Clean Architecture principles, it leverages native System.Threading.Channels for maximum throughput and offers a reactive Blazor Server dashboard powered by SignalR for real-time monitoring.

## Key Features

* **Zero-Dependency Core:** The engine logic (`ChokaQ.Core`) relies strictly on standard .NET abstractions, keeping your dependency tree clean.
* **Efficient SQL Storage:** The optional SQL Server provider uses **Dapper** as a lightweight micro-ORM for maximum performance over raw ADO.NET.
* **Resilient Architecture:** Built-in implementation of Circuit Breaker patterns and Retry Policies with exponential backoff to handle transient failures.
* **Robust Storage:** Pluggable storage architecture.
    * **In-Memory:** For development and testing.
    * **SQL Server:** Production-grade persistence using Dapper. Features atomic locking (ROWLOCK, READPAST, UPDLOCK) to safely handle concurrency across multiple worker nodes.
* **Real-Time Dashboard:** A zero-config UI built on Blazor Server.
    * **Live Updates:** Powered by SignalR, requiring no page refreshes.
    * **Active Job Zone:** Dedicated view for currently executing jobs.
    * **Job Inspector:** Detailed view of job payloads, stack traces, and execution timelines.
    * **Theming:** Includes multiple built-in themes (Office, Night Shift, High Contrast, etc.).
* **Developer Friendly:** Strongly-typed jobs and seamless Dependency Injection integration.

## Installation

Currently, ChokaQ is available as a source-only library. Clone the repository to integrate it into your solution.

```bash
git clone https://github.com/your-username/ChokaQ.git
```

## Quick Start

### 1. Register Services

In your `Program.cs`, register the core services, storage, and dashboard.

```csharp
var builder = WebApplication.CreateBuilder(args);

// 1. Add Core Services
builder.Services.AddChokaQ();

// 2. Configure Storage (SQL Server)
builder.Services.UseSqlServer(options =>
{
    options.ConnectionString = builder.Configuration.GetConnectionString("ChokaQDb");
    options.SchemaName = "chokaq";
    options.AutoCreateSqlTable = true; // Auto-provision schema on startup
});

// 3. Add Dashboard
builder.Services.AddChokaQDashboard(options =>
{
    options.RoutePrefix = "/chokaq";
});
```

### 2. Define a Job

Create a record that implements `IChokaQJob`. You can inherit from `ChokaQBaseJob` to handle unique ID generation automatically.

```csharp
using ChokaQ.Abstractions;

public record SendEmailJob(string Email, string Subject) : ChokaQBaseJob;
```

### 3. Create a Handler

Implement `IChokaQJobHandler<T>`. This class is registered automatically and supports Dependency Injection.

```csharp
using ChokaQ.Abstractions;

public class SendEmailHandler : IChokaQJobHandler<SendEmailJob>
{
    private readonly ILogger<SendEmailHandler> _logger;

    public SendEmailHandler(ILogger<SendEmailHandler> logger)
    {
        _logger = logger;
    }

    public async Task HandleAsync(SendEmailJob job, CancellationToken ct)
    {
        _logger.LogInformation("Sending email to {Email}...", job.Email);
        
        // Simulate actual work
        await Task.Delay(1000, ct); 
        
        _logger.LogInformation("Email sent successfully.");
    }
}
```

### 4. Enqueue Jobs

Inject `IChokaQQueue` into your API endpoints or services to dispatch jobs.

```csharp
app.MapPost("/send-email", async (IChokaQQueue queue) =>
{
    var job = new SendEmailJob("user@example.com", "Welcome to ChokaQ");
    
    // Enqueue with priority (Default: 10)
    await queue.EnqueueAsync(job, priority: 20, createdBy: "API_User");
    
    return Results.Ok(new { JobId = job.Id });
});
```

## Dashboard Overview

Access the dashboard at `/chokaq` (or your configured route prefix).

### Layout
The dashboard is divided into two logical zones to improve usability:
1.  **Active Zone (Top):** Displays cards for jobs currently in the "Processing" state. This allows immediate visibility of active workers.
2.  **History Zone (Bottom):** A virtualized list displaying the Queue (Pending) and History (Succeeded/Failed/Cancelled).

### Features
* **Circuit Monitor:** Real-time health check of all Job Types. If a job type fails repeatedly, the circuit opens, and the status is reflected here.
* **Job Inspector:** Click on any job row to open a detailed panel containing the JSON payload, timestamps, and error details.
* **Controls:** Supports manual Cancellation and Restarting of jobs directly from the UI.

## Architecture Details

### Core Engine
The core engine relies on `System.Threading.Channels` to act as a high-throughput buffer. This decouples the Producer (API) from the Consumer (Worker).

### SQL Persistence
When using SQL Server, ChokaQ acts as a Polling Consumer.
1.  **Enqueue:** The job is serialized to JSON and saved to the `[chokaq].[Jobs]` table.
2.  **Poll:** The `SqlJobWorker` polls the database using an optimized query with `ROWLOCK, READPAST, UPDLOCK` hints. This ensures that multiple API instances (web farm scenarios) never process the same job twice.

### Resilience
* **Circuit Breaker:** If a job type fails repeatedly (default threshold: 5), the circuit opens, preventing further execution for a set duration to allow the downstream system to recover.
* **Idempotency:** Pass an optional `IdempotencyKey` during enqueue to prevent duplicate processing of the same business event.

## License

This project is licensed under the MIT License.