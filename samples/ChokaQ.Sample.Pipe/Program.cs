using ChokaQ.Core.Extensions;
using ChokaQ.Sample.Pipe.Components;
using ChokaQ.Sample.Pipe.Services;
using ChokaQ.TheDeck.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// --- CHOKAQ PIPE CONFIGURATION ---
builder.Services.AddChokaQ(options => options.UsePipe<GlobalPipeHandler>());

builder.Services.AddHealthChecks()
    // Pipe mode still benefits from the generic ChokaQ checks: the host can verify that the
    // worker is alive and that in-memory queue lag is inside the configured operational window.
    .AddChokaQHealthChecks(builder.Configuration.GetSection("ChokaQ:Health"));

builder.Services.AddChokaQTheDeck();

var app = builder.Build();

// Configure the HTTP request pipeline.
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

app.MapChokaQTheDeck();
app.MapHealthChecks("/health");

app.Run();
