using ChokaQ.Abstractions;
using ChokaQ.Core.Extensions;
using ChokaQ.Dashboard.Extensions;
using ChokaQ.SampleApp.Components;
using ChokaQ.SampleApp.Handlers;
using ChokaQ.SampleApp.Jobs;

var builder = WebApplication.CreateBuilder(args);

// --- SERVICES ---

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Enable Controllers for our API
builder.Services.AddControllers();
builder.Services.AddHttpClient();

// === ChokaQ Stack ===
builder.Services.AddChokaQ();           // Core services (Queue, Storage, Worker)
builder.Services.AddChokaQDashboard();  // Dashboard services (SignalR, Notifiers)

// Register the job handler
// DI will automatically inject IJobContext and ILogger into the constructor
builder.Services.AddTransient<IChokaQJobHandler<PrintMessageJob>, PrintMessageJobHandler>();
// ====================

var app = builder.Build();

// --- PIPELINE ---

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

// Map Controllers (Enables api/jobs endpoints)
app.MapControllers();

// Enable SignalR Hub for the Dashboard
app.UseChokaQDashboard();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();