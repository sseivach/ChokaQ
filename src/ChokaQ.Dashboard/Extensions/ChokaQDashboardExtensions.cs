using ChokaQ.Abstractions;
using ChokaQ.Dashboard.Components.Layout;
using ChokaQ.Dashboard.Hubs;
using ChokaQ.Dashboard.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ChokaQ; // Root namespace for easier discovery

public static class ChokaQDashboardExtensions
{
    public static IServiceCollection AddChokaQDashboard(this IServiceCollection services)
    {
        services.AddSignalR();
        services.AddSingleton<IChokaQNotifier, ChokaQSignalRNotifier>();
        return services;
    }

    public static WebApplication MapChokaQDashboard(this WebApplication app, string path = "/chokaq")
    {
        // Ensure path starts with /
        if (!path.StartsWith("/")) path = "/" + path;

        // 1. Map Static Assets (CSS, JS from the Razor Class Library)
        // This allows serving files from _content/ChokaQ.Dashboard/
        app.UseStaticFiles();
        app.UseAntiforgery();

        // 2. Map the SignalR Hub
        app.MapHub<ChokaQHub>($"{path}/hub");

        // 3. Map the Blazor Page (Host) to the specific path
        // We create a Group to isolate our dashboard endpoints
        var dashboardGroup = app.MapGroup(path);

        dashboardGroup.MapRazorComponents<DashboardHost>()
                      .AddInteractiveServerRenderMode();

        return app;
    }
}