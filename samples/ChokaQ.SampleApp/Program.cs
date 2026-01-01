using ChokaQ.Abstractions;
using ChokaQ.Core.Queues;
using ChokaQ.Core.Workers;
using ChokaQ.Core.Jobs;
using ChokaQ.Core.Handlers;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// Register the Queue as Singleton (The "Pipe")
builder.Services.AddSingleton<InMemoryQueue>();
// Alias for the interface
builder.Services.AddSingleton<IChokaQQueue>(sp => sp.GetRequiredService<InMemoryQueue>());

// Register the Background Worker
builder.Services.AddHostedService<JobWorker>();

// Register the Handler for our test job
builder.Services.AddTransient<IChokaQJobHandler<PrintMessageJob>, PrintMessageJobHandler>();

// ==========================================

var app = builder.Build();

app.MapGet("/", () => "ChokaQ Sample App is running! Use POST /enqueue to test.");

// 2. TEST ENDPOINT
// We inject the queue interface and push a job manually.
app.MapPost("/enqueue", async (IChokaQQueue queue, [FromBody] string text) =>
{
    var job = new PrintMessageJob(text);

    // Fire and forget!
    await queue.EnqueueAsync(job);

    return Results.Accepted(value: new { Status = "Enqueued", Payload = text });
});

app.Run();