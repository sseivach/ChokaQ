# ChokaQ 🍫

**ChokaQ** is a lightweight, real-time background job processing library for .NET 10.
Built with **Blazor**, **SignalR**, and **System.Threading.Channels**, it provides a modern dashboard and seamless developer experience.

![ChokaQ Dashboard](https://via.placeholder.com/800x400?text=ChokaQ+Mission+Control)

## 🚀 Features

* **Fire-and-Forget**: Offload heavy tasks to the background instantly.
* **Real-time Dashboard**: Watch your jobs update live (powered by SignalR).
* **Zero Dependencies**: Core logic uses native .NET Channels.
* **Extensible Storage**: In-Memory by default, SQL Server ready.
* **Clean Architecture**: Separation of concerns (Abstractions, Core, UI).

## 📦 Project Structure

* `src/ChokaQ.Abstractions` - Interfaces and DTOs (The contracts).
* `src/ChokaQ.Core` - The engine (Queue, Worker, Handlers).
* `src/ChokaQ.Dashboard` - Blazor UI components library.
* `samples/ChokaQ.SampleApp` - Demo application showing it all in action.

## ⚡ Quick Start

### 1. Define a Job
Implement `IChokaQJob` to define your payload.

```csharp
public record SendEmailJob(string Email, string Subject) : IChokaQJob
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
}
```

### 2. Create a Handler
Implement `IChokaQJobHandler<T>`.

```csharp
public class SendEmailHandler : IChokaQJobHandler<SendEmailJob>
{
    public async Task HandleAsync(SendEmailJob job, CancellationToken ct)
    {
        // Simulate sending email
        await Task.Delay(1000);
        Console.WriteLine($"Email sent to {job.Email}");
    }
}
```

### 3. Enqueue
Inject `IChokaQQueue` and fire away!

```csharp
app.MapPost("/send", async (IChokaQQueue queue) => 
{
    await queue.EnqueueAsync(new SendEmailJob("test@example.com", "Hello!"));
    return Results.Ok("Job Enqueued");
});
```

## 🛠️ Tech Stack

* **.NET 10** (Preview)
* **Blazor Server** (Interactive Components)
* **SignalR** (Real-time WebSockets)
* **Bootstrap 5** (UI Styling)

## 📝 License

MIT License. Made with ❤️ by Sergei Seivach.