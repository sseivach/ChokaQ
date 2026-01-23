using ChokaQ;
using ChokaQ.Core.Extensions;
using ChokaQ.Storage.SqlServer;
using ChokaQ.SampleRun.Components;
using ChokaQ.SampleRun.Profiles;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// --- CHOKAQ CONFIGURATION ---
builder.Services.AddChokaQ(options =>
{
    // Register separate profiles for logical separation
    options.AddProfile<MailingProfile>();
    options.AddProfile<ReportingProfile>();
    options.AddProfile<SystemProfile>();
});

builder.Services.UseSqlServer(options =>
{
    options.ConnectionString = builder.Configuration.GetConnectionString("ChokaQDb")
        ?? throw new InvalidOperationException("Conn string not found");
    options.SchemaName = "chokaq";
    options.AutoCreateSqlTable = builder.Environment.IsDevelopment();
});

builder.Services.AddChokaQDashboard(options =>
{
    options.RoutePrefix = "/chokaq";
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapChokaQDashboard();

app.Run();