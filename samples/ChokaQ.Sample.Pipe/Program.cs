using ChokaQ;
using ChokaQ.Core.Extensions;
using ChokaQ.Sample.Pipe.Components;
using ChokaQ.Sample.Pipe.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// --- CHOKAQ PIPE CONFIGURATION ---
builder.Services.AddChokaQ(options => options.UsePipe<GlobalPipeHandler>());

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

app.Run();