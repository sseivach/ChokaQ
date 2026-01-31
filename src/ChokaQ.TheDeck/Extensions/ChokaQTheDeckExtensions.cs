using ChokaQ.Abstractions.Notifications;
using ChokaQ.TheDeck;
using ChokaQ.TheDeck.Components.Layout;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace ChokaQ;

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
        
        // TODO: Register ChokaQSignalRNotifier here when we move it to Core or replicate it.
        // For now, we skip the notifier or rely on Core if it's there. 
        // Wait, ChokaQSignalRNotifier was in Dashboard/Services. 
        // We need to migrate that service too or TheDeck won't get updates!
        // For this step (Skeleton), we'll skip the notifier registration to avoid compile errors 
        // until we create the Notifier service in TheDeck.
        
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

        // app.MapHub<ChokaQHub>($"{path}/hub"); // Hub also needs migration

        var dashboardGroup = app.MapGroup(path);
        dashboardGroup.MapRazorComponents<TheDeckHost>()
                      .AddInteractiveServerRenderMode();

        return app;
    }
}
