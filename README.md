# ChokaQ

![.NET 10](https://img.shields.io/badge/.NET-10.0%20-blue)
![License](https://img.shields.io/badge/license-MIT-green)
![Status](https://img.shields.io/badge/status-Active%20Development-orange)

**Current Status:** Work in Progress / Proof of Concept

ChokaQ is a lightweight, high-performance background job engine designed explicitly for .NET 10.
It bridges the gap between simple in-memory queues and heavy enterprise service buses.

## The 2x2 Architecture Matrix

ChokaQ is built on a modular architecture where **Processing Strategy** (The Brain) and **Storage Strategy** (The Memory) are completely independent. You can mix and match them to fit your exact needs.

| | **In-Memory Storage** (Default) | **SQL Server Storage** (Persistent) |
| :--- | :--- | :--- |
| **Bus Mode**<br>(Typed Profiles) | **Dev / Test / Light Tasks**<br>Type-safe logic, instant execution, but volatile. | **Enterprise Standard**<br>Type-safe, reliable, history, retries after restart. |
| **Pipe Mode**<br>(Raw String/JSON) | **"Rocket Mode"**<br>Maximum throughput, zero overhead, fire-and-forget. | **Integration / Proxy**<br>Store raw payloads from external systems without typed DTOs. |

---

## Configuration Guide

### Step 1: Choose Your Storage (Memory vs. SQL)

**Option A: SQL Server (Persistent)**
Use this for production workloads where data loss is not acceptable.
```csharp
builder.Services.UseSqlServer(options =>
{
    options.ConnectionString = builder.Configuration.GetConnectionString("ChokaQDb");
    options.AutoCreateSqlTable = true;
});
```

**Option B: In-Memory (Volatile)**
Simply **do nothing**. If you don't configure SQL, ChokaQ defaults to high-performance `System.Threading.Channels`.
*Note: Jobs are lost if the application restarts.*

---

### Step 2: Choose Your Processing (Bus vs. Pipe)

**Option A: The Enterprise Bus (Recommended)**
Best for clean architecture. You define DTOs, Handlers, and group them into Profiles.

*1. Create a Profile*
```csharp
public class MailingProfile : ChokaQJobProfile
{
    public MailingProfile()
    {
        // Map "send_email" key -> EmailJob DTO -> EmailHandler
        CreateJob<EmailJob, EmailHandler>("send_email");
    }
}
```

*2. Register*
```csharp
builder.Services.AddChokaQ(options =>
{
    options.AddProfile<MailingProfile>();
});
```

**Option B: The Raw Pipe**
Best for simple consumers or dynamic routing. You receive the raw `type` string and `payload` JSON.

*1. Create a Pipe Handler*
```csharp
public class MyPipe : IChokaQPipeHandler
{
    public async Task HandleAsync(string type, string payload, CancellationToken ct)
    {
        if (type == "fast_event") Console.WriteLine(payload);
    }
}
```

*2. Register*
```csharp
builder.Services.AddChokaQ(options =>
{
    options.UsePipe<MyPipe>();
});
```

---

## Examples of Combinations

### 1. The "Enterprise Standard" (Bus + SQL)
*Use case: Critical business transactions, emails, reports.*
```csharp
builder.Services.AddChokaQ(opt => opt.AddProfile<BusinessProfile>());
builder.Services.UseSqlServer(opt => opt.ConnectionString = "...");
```

### 2. The "Rocket Mode" (Pipe + Memory)
*Use case: Fire-and-forget metrics, logs, non-critical notifications.*
```csharp
// No SQL configuration needed
builder.Services.AddChokaQ(opt => opt.UsePipe<FastHandler>());
```

### 3. The "Dev Sandbox" (Bus + Memory)
*Use case: Developing with typed jobs locally without needing a DB.*
```csharp
// No SQL configuration needed
builder.Services.AddChokaQ(opt => opt.AddProfile<BusinessProfile>());
```

---

## Dashboard

Regardless of the mode, the dashboard works out of the box (though In-Memory history is lost on restart).

```csharp
builder.Services.AddChokaQDashboard(options => options.RoutePrefix = "/chokaq");
```

## Author

Created by **Sergei Seivach**.

## License

This project is licensed under the MIT License.