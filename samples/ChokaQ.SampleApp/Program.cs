using ChokaQ.Abstractions;
using ChokaQ.Core.Extensions;
using ChokaQ.Dashboard;
using ChokaQ.Core.Jobs;
using ChokaQ.Core.Handlers;
using ChokaQ.SampleApp.Components;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Register HttpClient to allow the Blazor components (like JobGenerator) 
// to call the API endpoints hosted in this same application.
builder.Services.AddHttpClient();

// ==========================================
// CHOKAQ REGISTRATION
// ==========================================

// 1. Core Services (Queue, Storage, Background Workers)
// This registers the essential infrastructure to run background jobs.
builder.Services.AddChokaQ();

// 2. Dashboard Services (SignalR Hub + Real-time Notifier)
// This connects the backend to the UI, enabling live updates.
builder.Services.AddChokaQDashboard();

// 3. Application Job Handlers
// Register the specific logic for processing PrintMessageJob.
builder.Services.AddTransient<IChokaQJobHandler<PrintMessageJob>, PrintMessageJobHandler>();

// ==========================================

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

// ==========================================
// CHOKAQ MIDDLEWARE
// ==========================================

// Maps the SignalR Hub required for the Dashboard to receive updates.
app.UseChokaQDashboard();

// ==========================================

// Map Blazor Components
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// ==========================================
// API ENDPOINTS (For Simulation & Testing)
// ==========================================

// 1. Bulk Job Generation Endpoint
// Used by the "Job Generator" UI component to fire multiple tasks at once.
app.MapPost("/api/simulation/bulk", async (IChokaQQueue queue, [FromBody] int count) =>
{
    if (count < 1 || count > 100)
    {
        return Results.BadRequest("Count must be between 1 and 100.");
    }

    var jobIds = new List<string>();

    for (int i = 0; i < count; i++)
    {
        // Create a new job instance with a timestamped message
        var job = new PrintMessageJob($"Bulk Task #{i + 1} generated at {DateTime.Now:HH:mm:ss}");

        // Enqueue the job for background processing
        await queue.EnqueueAsync(job);

        jobIds.Add(job.Id);
    }

    return Results.Ok(new
    {
        Message = $"Successfully enqueued {count} jobs.",
        JobIds = jobIds
    });
});

// 2. Single Job Endpoint (Manual Test)
// Allows triggering a specific text message job via API tools (like Postman or curl).
app.MapPost("/enqueue", async (IChokaQQueue queue, [FromBody] string text) =>
{
    var job = new PrintMessageJob(text);
    await queue.EnqueueAsync(job);
    return Results.Accepted(value: new { Status = "Enqueued", JobId = job.Id, Payload = text });
});

app.Run();