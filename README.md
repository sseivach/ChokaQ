# ChokaQ

![.NET 10](https://img.shields.io/badge/.NET-10.0%20-blue)
![License](https://img.shields.io/badge/license-MIT-green)
![Status](https://img.shields.io/badge/status-Active%20Development-orange)

**Current Status:** Work in Progress / Proof of Concept

ChokaQ is a lightweight, high-performance background job engine designed explicitly for .NET 10.
It bridges the gap between simple in-memory queues and heavy enterprise service buses.

## Key Features

* **Hybrid Engine:** Choose between **Type-Safe Bus** (clean architecture) or **Raw Pipe** (high performance) modes.
* **Explicit Configuration:** No magic scanning. Use **Profiles** to strictly define your job topology.
* **Zero-Dependency Core:** The engine logic (`ChokaQ.Core`) relies strictly on standard .NET abstractions.
* **Efficient SQL Storage:** The optional SQL Server provider uses **Dapper** with `ROWLOCK/READPAST` for maximum throughput.
* **Real-Time Dashboard:** A zero-config UI built on Blazor Server + SignalR.
* **Resilient Architecture:** Built-in Circuit Breaker, Retries with Exponential Backoff, and Idempotency support.

## Installation

Currently, ChokaQ is available as a source-only library.
Clone the repository to integrate it into your solution.

```bash
git clone https://github.com/your-username/ChokaQ.git
```

## Getting Started

In `Program.cs`, register the core services and storage. Then choose your operating mode.

```csharp
var builder = WebApplication.CreateBuilder(args);

// 1. Configure Storage (SQL Server)
builder.Services.UseSqlServer(options =>
{
    options.ConnectionString = builder.Configuration.GetConnectionString("ChokaQDb");
    options.SchemaName = "chokaq";
    options.AutoCreateSqlTable = true;
});

// 2. Add Dashboard (Optional)
builder.Services.AddChokaQDashboard(options => options.RoutePrefix = "/chokaq");
```

---

### Mode A: The Enterprise Bus (Recommended)
Best for clean architecture, type safety, and maintainability.

**1. Define DTO & Handler**
```csharp
public record EmailJob(string To, string Subject) : ChokaQBaseJob;

public class EmailHandler : IChokaQJobHandler<EmailJob>
{
    public async Task HandleAsync(EmailJob job, CancellationToken ct)
    {
        Console.WriteLine($"Sending email to {job.To}...");
    }
}
```

**2. Create a Profile**
Group your jobs logically (e.g., `MailingProfile.cs`).
```csharp
public class MailingProfile : ChokaQJobProfile
{
    public MailingProfile()
    {
        // Explicitly map the string Key to the C# Type and Handler
        CreateJob<EmailJob, EmailHandler>("send_email_v1");
    }
}
```

**3. Register**
```csharp
builder.Services.AddChokaQ(options =>
{
    options.AddProfile<MailingProfile>();
});
```

---

### Mode B: The Raw Pipe
Best for simple consumers, proxies, or dynamic routing where you don't want typed DTOs.

**1. Implement Pipe Handler**
You get the raw `jobType` string and the `payload` JSON.
```csharp
public class GlobalPipeHandler : IChokaQPipeHandler
{
    public async Task HandleAsync(string jobType, string payload, CancellationToken ct)
    {
        switch (jobType)
        {
            case "legacy_job":
                // Parse payload manually and execute
                break;
        }
    }
}
```

**2. Register**
```csharp
builder.Services.AddChokaQ(options =>
{
    options.UsePipe<GlobalPipeHandler>();
});
```

---

## Enqueue Jobs

Inject `IChokaQQueue` into your API endpoints or services.

```csharp
app.MapPost("/send-email", async (IChokaQQueue queue) =>
{
    var job = new EmailJob("user@example.com", "Welcome!");
    
    // The engine automatically resolves the key "send_email_v1" from your Profile
    await queue.EnqueueAsync(job, priority: 20, queue: "emails");
    
    return Results.Ok(new { JobId = job.Id });
});
```

## Architecture Details

### SQL Persistence
When using SQL Server, ChokaQ acts as a Polling Consumer.
1.  **Enqueue:** The job is serialized to JSON and saved to the `[chokaq].[Jobs]` table.
2.  **Poll:** The `SqlJobWorker` polls the database using an optimized query with `ROWLOCK, READPAST, UPDLOCK` hints.
This ensures that multiple API instances (web farm scenarios) never process the same job twice.

### Circuit Breaker
If a job type fails repeatedly (default threshold: 5), the circuit opens, preventing further execution for a set duration. This protects your downstream services from being overwhelmed during outages.

## License

This project is licensed under the MIT License.