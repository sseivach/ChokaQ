using ChokaQ.Abstractions;
using ChokaQ.Dashboard.Hubs;
using ChokaQ.Dashboard.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace ChokaQ.Dashboard;

public static class ChokaQDashboardExtensions
{
    public static IServiceCollection AddChokaQDashboard(this IServiceCollection services)
    {
        services.AddSignalR();

        services.AddSingleton<IChokaQNotifier, ChokaQSignalRNotifier>();

        return services;
    }

    public static IApplicationBuilder UseChokaQDashboard(this WebApplication app)
    {
        app.MapHub<ChokaQHub>("/chokaq-hub");

        return app;
    }
}