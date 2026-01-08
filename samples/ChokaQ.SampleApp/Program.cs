using ChokaQ.Abstractions;
using ChokaQ.Core.Extensions;
using ChokaQ.Dashboard.Extensions;
using ChokaQ.Storage.SqlServer;
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

// 1. Core Services (JobQueue, BackgroundWorker, etc.)
// By default, this registers InMemory storage.
builder.Services.AddChokaQ();

// 2. Storage Strategy: Switch to SQL Server
// This overrides the default InMemory storage with our SqlJobStorage.
builder.Services.UseSqlServer(options =>
{
    // Retrieve connection string from appsettings.json
    var connString = builder.Configuration.GetConnectionString("ChokaQDb");

    if (string.IsNullOrWhiteSpace(connString))
    {
        throw new InvalidOperationException("Connection string 'ChokaQDb' is missing in appsettings.json.");
    }

    options.ConnectionString = connString;

    // Define the schema name (matches our SQL scripts logic)
    options.SchemaName = "chokaq";

    // Auto-Provisioning:
    // Automatically runs SchemaTemplate.sql and CleanupProcTemplate.sql at startup.
    // ENABLE only for Development environments to simplify setup.
    // DISABLE for Production (let DBAs handle migrations).
    options.AutoCreateSqlTable = builder.Environment.IsDevelopment();
});

// 3. Dashboard Services (SignalR, Notifiers)
builder.Services.AddChokaQDashboard();

// 4. Job Handlers Registration
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