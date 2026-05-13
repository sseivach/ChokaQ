using ChokaQ.Abstractions.Notifications;
using ChokaQ.TheDeck.UI.Layout;
using ChokaQ.TheDeck.Hubs;
using ChokaQ.TheDeck.Services;
using Microsoft.AspNetCore.Authorization;
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
        ValidateOptions(options);
        services.AddSingleton(options);

        services.AddRazorComponents()
                .AddInteractiveServerComponents();

        services.AddAntiforgery();
        services.AddAuthorization();
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
        // The Deck is a write-capable operations console. Secure by default means every mapped
        // endpoint requires authorization unless the host explicitly opts into anonymous access.
        // A named policy lets production apps split admin access from their normal user policy.
        if (options.AllowAnonymousDeck)
        {
            hubEndpoint.AllowAnonymous();
            dashboardGroup.AllowAnonymous();
        }
        else if (!string.IsNullOrWhiteSpace(options.AuthorizationPolicy))
        {
            hubEndpoint.RequireAuthorization(options.AuthorizationPolicy);
            dashboardGroup.RequireAuthorization(options.AuthorizationPolicy);
        }
        else
        {
            hubEndpoint.RequireAuthorization();
            dashboardGroup.RequireAuthorization();
        }
        // ----------------------

        dashboardGroup.MapRazorComponents<TheDeckHost>()
                      .AddInteractiveServerRenderMode();

        return app;
    }

    private static void ValidateOptions(ChokaQTheDeckOptions options)
    {
        if (options.AllowAnonymousDeck &&
            (!string.IsNullOrWhiteSpace(options.AuthorizationPolicy) ||
             !string.IsNullOrWhiteSpace(options.DestructiveAuthorizationPolicy)))
        {
            // These two settings are deliberately mutually exclusive. Combining them would make
            // the public/security posture ambiguous, and security configuration should fail fast.
            throw new InvalidOperationException(
                "ChokaQ The Deck cannot be both anonymous and policy-protected. Set either AllowAnonymousDeck = true or authorization policies, not both.");
        }

        if (options.QueueLagWarningThresholdSeconds < 0 ||
            options.QueueLagCriticalThresholdSeconds <= options.QueueLagWarningThresholdSeconds)
        {
            // Threshold validation prevents a subtle operator-experience bug: if warning and
            // critical bands overlap, the same queue can appear healthy in one refresh and
            // critical in the next even when the measured lag barely changed.
            throw new InvalidOperationException(
                "ChokaQ The Deck queue lag thresholds must be non-negative and critical must be greater than warning.");
        }
    }
}
