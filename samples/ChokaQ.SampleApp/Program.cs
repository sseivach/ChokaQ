using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Core.Handlers;
using ChokaQ.Core.Jobs;
using ChokaQ.Core.Queues;
using ChokaQ.Core.Storage;
using ChokaQ.Core.Workers;
using ChokaQ.SampleApp.Components;
using ChokaQ.SampleApp.Hubs;
using ChokaQ.SampleApp.Services;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// 1. Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(); // Enables SignalR for components

builder.Services.AddSignalR(); // Required for the Hub

// ==========================================
// 2. ChokaQ Services Registration
// ==========================================

builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<IJobStorage, InMemoryJobStorage>();
builder.Services.AddSingleton<InMemoryQueue>();

// Alias for the queue interface
builder.Services.AddSingleton<IChokaQQueue>(sp => sp.GetRequiredService<InMemoryQueue>());

// Register the Real-time Notifier (SignalR implementation)
builder.Services.AddSingleton<IChokaQNotifier, SignalRNotifier>();

// Register the Background Worker
builder.Services.AddHostedService<JobWorker>();

// Register the Handler for the test job
builder.Services.AddTransient<IChokaQJobHandler<PrintMessageJob>, PrintMessageJobHandler>();

// ==========================================

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();

// This middleware serves static files (CSS, JS, images).
// In the new Blazor template, this handles static web assets automatically.
app.UseStaticFiles();

app.UseAntiforgery();

// Map the SignalR Hub
app.MapHub<ChokaQHub>("/chokaq-hub");

// Map Blazor Components
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Test Endpoint to push jobs via cURL / Postman
app.MapPost("/enqueue", async (IChokaQQueue queue, [FromBody] string text) =>
{
    var job = new PrintMessageJob(text);
    await queue.EnqueueAsync(job);
    return Results.Accepted(value: new { Status = "Enqueued", Payload = text });
});

app.Run();