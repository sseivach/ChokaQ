using ChokaQ.Abstractions;
using ChokaQ.Dashboard;
using ChokaQ.Dashboard.Components.Layout;
using ChokaQ.Dashboard.Hubs;
using ChokaQ.Dashboard.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace ChokaQ;
// Root namespace for easier discovery

public static class ChokaQDashboardExtensions
{
    public static IServiceCollection AddChokaQDashboard(
        this IServiceCollection services,
        Action<ChokaQDashboardOptions>? configure = null)
    {
        // 1. Configure Options
        var options = new ChokaQDashboardOptions();
        configure?.Invoke(options);

        // Register as Singleton so we can access it everywhere
        services.AddSingleton(options);

        // 2. Add Blazor & Core Dependencies (The "Sidecar" logic)
        // Since we are running inside potentially a pure Web API, we must ensure
        // Blazor services are registered for the Dashboard to work.
        services.AddRazorComponents()
                .AddInteractiveServerComponents();

        // Required explicitly because we use app.UseAntiforgery() in the mapping
        services.AddAntiforgery();

        // 3. Add SignalR
        services.AddSignalR();
        services.AddSingleton<IChokaQNotifier, ChokaQSignalRNotifier>();

        return services;
    }

    public static WebApplication MapChokaQDashboard(this WebApplication app)
    {
        // 1. Resolve Options
        var options = app.Services.GetRequiredService<ChokaQDashboardOptions>();
        var path = options.RoutePrefix;

        if (!path.StartsWith("/")) path = "/" + path;
        path = path.TrimEnd('/');

        // 2. Static Files & Security
        app.UseStaticFiles();
        app.UseAntiforgery(); // This requires AddAntiforgery() to be called above

        // 3. Map SignalR
        app.MapHub<ChokaQHub>($"{path}/hub");

        // 4. Map Blazor Host
        var dashboardGroup = app.MapGroup(path);

        dashboardGroup.MapRazorComponents<DashboardHost>()
                      .AddInteractiveServerRenderMode();

        return app;
    }
}