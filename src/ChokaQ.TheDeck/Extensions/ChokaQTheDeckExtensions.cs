using ChokaQ.Abstractions.Notifications;
using ChokaQ.TheDeck;
using ChokaQ.TheDeck.Hubs;
using ChokaQ.TheDeck.Services;
using ChokaQ.TheDeck.Components.Layout;
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

        app.MapHub<ChokaQHub>($"{path}/hub");

        var dashboardGroup = app.MapGroup(path);
        dashboardGroup.MapRazorComponents<TheDeckHost>()
                      .AddInteractiveServerRenderMode();

        return app;
    }
}
