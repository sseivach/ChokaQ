using ChokaQ.Core.Extensions;
using ChokaQ.Sample.Bus.Components;
using ChokaQ.Sample.Bus.Profiles;
using ChokaQ.Storage.SqlServer;
using ChokaQ.TheDeck.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// --- CHOKAQ CONFIGURATION ---
builder.Services.AddChokaQ(
    builder.Configuration.GetSection("ChokaQ"),
    options =>
    {
        // Runtime policy belongs in appsettings; profiles are compile-time registrations.
        // Keeping those responsibilities separate makes the sample match real NuGet usage:
        // operators tune timeouts/retries without recompiling, developers own job types.
        options.AddProfile<MailingProfile>();
        options.AddProfile<ReportingProfile>();
        options.AddProfile<SystemProfile>();
    });

builder.Services.UseSqlServer(
    builder.Configuration.GetSection("ChokaQ:SqlServer"),
    options =>
    {
        var connectionString = options.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = builder.Configuration.GetConnectionString("ChokaQDb")
                ?? Environment.GetEnvironmentVariable("CHOKAQ_SAMPLE_SQL");
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Samples should teach safe production habits. Keep real passwords out of appsettings
            // and provide the connection string via user-secrets, environment variables, or local
            // launch configuration instead.
            throw new InvalidOperationException(
                "Configure ChokaQ:SqlServer:ConnectionString, ConnectionStrings:ChokaQDb, or CHOKAQ_SAMPLE_SQL before running the SQL sample.");
        }

        options.ConnectionString = connectionString;
        options.AutoCreateSqlTable = builder.Environment.IsDevelopment();
    });

builder.Services.AddHealthChecks()
    // Health checks are registered through the standard ASP.NET Core pipeline because hosts
    // already know how to publish, protect, and scrape /health endpoints. ChokaQ contributes
    // domain checks: SQL schema reachability, worker liveness, and queue saturation.
    .AddChokaQSqlServerHealthChecks(builder.Configuration.GetSection("ChokaQ:Health"));

builder.Services.AddChokaQTheDeck(options =>
{
    options.RoutePrefix = "/chokaq";
    // The sample opts into anonymous Deck access only in Development. Real applications
    // should configure AddAuthentication/AddAuthorization and leave this false.
    options.AllowAnonymousDeck = builder.Environment.IsDevelopment();
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

if (!builder.Configuration.GetValue<bool>("DOTNET_RUNNING_IN_CONTAINER"))
{
    // The Docker Compose sample intentionally exposes HTTP on an internal container port and
    // lets the host decide whether TLS is terminated by a reverse proxy. Keeping HTTPS
    // redirection for normal local runs still teaches the secure default without producing
    // noisy "failed to determine HTTPS port" warnings in containers.
    app.UseHttpsRedirection();
}
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapChokaQTheDeck();
app.MapHealthChecks("/health");

app.Run();
