using ChokaQ.Core.Extensions;
using ChokaQ.Sample.NuGetLab.Endpoints;
using ChokaQ.Sample.NuGetLab.Infrastructure;
using ChokaQ.Sample.NuGetLab.Jobs;
using ChokaQ.Sample.NuGetLab.Middleware;
using ChokaQ.Storage.SqlServer;
using ChokaQ.TheDeck.Extensions;

var builder = WebApplication.CreateBuilder(args);
var sqlConnectionString = SqlConnectionStringResolver.Resolve(builder.Configuration);

SqlDatabaseBootstrapper.EnsureDatabaseExists(sqlConnectionString);

builder.Services
    .AddChokaQ(
        builder.Configuration.GetSection("ChokaQ"),
        options =>
        {
            options.AddProfile<LabProfile>();
            options.AddMiddleware<LabAuditMiddleware>();
        })
    .AddResultIdempotency();

builder.Services.UseSqlServer(
    builder.Configuration.GetSection("ChokaQ:SqlServer"),
    options =>
    {
        options.ConnectionString = sqlConnectionString;
        options.AutoCreateSqlTable = true;
    });

builder.Services.AddHealthChecks()
    .AddChokaQSqlServerHealthChecks(builder.Configuration.GetSection("ChokaQ:Health"));

builder.Services.AddChokaQTheDeck(options =>
{
    options.RoutePrefix = "/chokaq";
    options.AllowAnonymousDeck = builder.Environment.IsDevelopment();
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapGet("/", () => Results.Redirect("/index.html"));
app.MapLabEndpoints();
app.MapChokaQTheDeck();
app.MapHealthChecks("/health");

app.Run();
