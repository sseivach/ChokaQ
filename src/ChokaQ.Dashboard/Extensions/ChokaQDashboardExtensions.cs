using ChokaQ.Abstractions.Notifications;
using ChokaQ.Dashboard;
using ChokaQ.Dashboard.Components.Layout;
using ChokaQ.Dashboard.Hubs;
using ChokaQ.Dashboard.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace ChokaQ;
// Root namespace for easier discovery by users

/// <summary>
/// Extension methods for adding the ChokaQ Dashboard to ASP.NET Core applications.
/// </summary>
public static class ChokaQDashboardExtensions
{
    /// <summary>
    /// Registers ChokaQ Dashboard services including Blazor Server and SignalR.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration for dashboard settings.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// Usage:
    /// <code>
    /// // In Program.cs
    /// builder.Services.AddChokaQ(options => options.AddProfile&lt;MyProfile&gt;());
    /// builder.Services.AddChokaQDashboard(options =>
    /// {
    ///     options.RoutePrefix = "/jobs";  // Dashboard URL
    /// });
    /// 
    /// var app = builder.Build();
    /// app.MapChokaQDashboard();
    /// app.Run();
    /// </code>
    /// 
    /// This registers:
    /// - Blazor Server components for the dashboard UI
    /// - SignalR hub for real-time job updates
    /// - IChokaQNotifier implementation (replaces NullNotifier)
    /// - Required antiforgery services
    /// </remarks>
    public static IServiceCollection AddChokaQDashboard(
        this IServiceCollection services,
        Action<ChokaQDashboardOptions>? configure = null)
    {
        // Configure options
        var options = new ChokaQDashboardOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        // Add Blazor Server (sidecar pattern - works in any ASP.NET Core app)
        services.AddRazorComponents()
                .AddInteractiveServerComponents();

        // Required for Blazor form protection
        services.AddAntiforgery();

        // Add SignalR for real-time updates
        services.AddSignalR();

        // Replace NullNotifier with SignalR implementation
        services.AddSingleton<IChokaQNotifier, ChokaQSignalRNotifier>();

        return services;
    }

    /// <summary>
    /// Maps the ChokaQ Dashboard endpoints to the application pipeline.
    /// </summary>
    /// <param name="app">The web application.</param>
    /// <returns>The web application for chaining.</returns>
    /// <remarks>
    /// This configures:
    /// - Static file serving for dashboard assets
    /// - SignalR hub at {RoutePrefix}/hub
    /// - Blazor Server rendering at {RoutePrefix}
    /// 
    /// Default route: /chokaq
    /// Access the dashboard at: https://yourapp.com/chokaq
    /// </remarks>
    public static WebApplication MapChokaQDashboard(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<ChokaQDashboardOptions>();
        var path = options.RoutePrefix;

        // Normalize path format
        if (!path.StartsWith("/")) path = "/" + path;
        path = path.TrimEnd('/');

        // Enable static files (CSS, JS) and CSRF protection
        app.UseStaticFiles();
        app.UseAntiforgery();

        // Map SignalR hub for real-time communication
        app.MapHub<ChokaQHub>($"{path}/hub");

        // Map Blazor Server components under the dashboard route
        var dashboardGroup = app.MapGroup(path);
        dashboardGroup.MapRazorComponents<DashboardHost>()
                      .AddInteractiveServerRenderMode();

        return app;
    }
}