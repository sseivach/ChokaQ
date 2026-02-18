using ChokaQ.Abstractions.Notifications;
using ChokaQ.TheDeck.UI.Layout;
using ChokaQ.TheDeck.Hubs;
using ChokaQ.TheDeck.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace ChokaQ.TheDeck.Extensions;

public static class ChokaQTheDeckExtensions
{
    public static IServiceCollection AddChokaQTheDeck(
        this IServiceCollection services,
        Action<ChokaQTheDeckOptions>? configure = null)
    {
        var options = new ChokaQTheDeckOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        services.AddRazorComponents()
                .AddInteractiveServerComponents();

        services.AddAntiforgery();
        services.AddSignalR();

        services.AddSingleton<IChokaQNotifier, ChokaQSignalRNotifier>();

        return services;
    }

    public static WebApplication MapChokaQTheDeck(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<ChokaQTheDeckOptions>();
        var path = options.RoutePrefix;

        if (!path.StartsWith("/")) path = "/" + path;
        path = path.TrimEnd('/');

        app.UseStaticFiles();
        app.UseAntiforgery();

        // 1. Map Hub
        var hubEndpoint = app.MapHub<ChokaQHub>($"{path}/hub");

        // 2. Create Dashboard Group
        var dashboardGroup = app.MapGroup(path);

        // --- SECURITY LAYER ---
        // If a policy is configured, enforce it on both the SignalR Hub and the UI Pages.
        if (!string.IsNullOrEmpty(options.AuthorizationPolicy))
        {
            hubEndpoint.RequireAuthorization(options.AuthorizationPolicy);
            dashboardGroup.RequireAuthorization(options.AuthorizationPolicy);
        }
        // ----------------------

        dashboardGroup.MapRazorComponents<TheDeckHost>()
                      .AddInteractiveServerRenderMode();

        return app;
    }
}