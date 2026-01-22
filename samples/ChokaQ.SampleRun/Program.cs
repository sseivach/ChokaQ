using ChokaQ;
using ChokaQ.Core.Extensions;
using ChokaQ.Storage.SqlServer;
using ChokaQ.SampleRun.Components;

var builder = WebApplication.CreateBuilder(args);

// 1. Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// 2. Add ChokaQ Core
builder.Services.AddChokaQ();
builder.Services.Configure<ChokaQ.Storage.SqlServer.SqlJobStorageOptions>(opt =>
{
});

// 3. Add SQL Storage
builder.Services.UseSqlServer(options =>
{
    options.ConnectionString = builder.Configuration.GetConnectionString("ChokaQDb")
        ?? throw new InvalidOperationException("Conn string not found");
    options.SchemaName = "chokaq";
    options.AutoCreateSqlTable = builder.Environment.IsDevelopment();
});

// 4. Add Dashboard (Sidecar)
builder.Services.AddChokaQDashboard(options =>
{
    options.RoutePrefix = "/chokaq";
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();

// IMPORTANT: Static files for Dashboard
app.UseStaticFiles();
app.UseAntiforgery();

// Map the Main App UI
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Map the Dashboard
app.MapChokaQDashboard();

app.Run();