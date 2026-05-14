using ChokaQ.Abstractions.Idempotency;
using ChokaQ.Abstractions.Middleware;
using ChokaQ.Core.Idempotency;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ChokaQ.Core.Extensions;

/// <summary>
/// Extension methods for registering the idempotency plugin.
/// </summary>
public static class IdempotencyExtensions
{
    /// <summary>
    /// Enables Level 2 claim-based idempotency for jobs implementing <see cref="IIdempotentJob"/>.
    ///
    /// [ARCHITECTURE PATTERN - "Opt-In Extension"]:
    /// This plugin is deliberately NOT included in the core AddChokaQ() registration.
    /// Most jobs do not need completion-marker storage. Adding it to the core would impose
    /// storage overhead on every job for a feature only some flows require.
    ///
    /// Adding this extension:
    ///   1. Registers the IdempotencyMiddleware at the FRONT of the execution pipeline.
    ///   2. Registers InMemoryIdempotencyStore as the default store (swap for Redis in prod).
    ///
    /// Usage:
    /// <code>
    /// // Development (in-memory store, zero config):
    /// services.AddChokaQ(opts => opts.AddProfile<MyProfile>())
    ///         .AddResultIdempotency();
    ///
    /// // Production (custom store, e.g., Redis):
    /// services.AddChokaQ(opts => opts.AddProfile<MyProfile>())
    ///         .AddResultIdempotency<RedisIdempotencyStore>();
    /// </code>
    /// </summary>
    public static IServiceCollection AddResultIdempotency(this IServiceCollection services)
        => services.AddResultIdempotency<InMemoryIdempotencyStore>();

    /// <summary>
    /// Enables Level 2 Idempotency with a custom <see cref="IIdempotencyStore"/> implementation.
    /// Custom production stores should also implement <see cref="IIdempotencyClaimStore"/>.
    /// </summary>
    /// <typeparam name="TStore">The store implementation to use (e.g., RedisIdempotencyStore).</typeparam>
    public static IServiceCollection AddResultIdempotency<TStore>(this IServiceCollection services)
        where TStore : class, IIdempotencyStore
    {
        // Register the store (TryAdd so user can override in tests)
        services.TryAddSingleton<IIdempotencyStore, TStore>();
        services.TryAddSingleton<IIdempotencyClaimStore>(sp =>
        {
            var store = sp.GetRequiredService<IIdempotencyStore>();
            return store as IIdempotencyClaimStore
                ?? new LegacyIdempotencyClaimStoreAdapter(store);
        });

        // Register the middleware — it will be prepended to the pipeline
        // IChokaQMiddleware registrations are ordered by registration order
        services.AddTransient<IChokaQMiddleware, IdempotencyMiddleware>();

        return services;
    }
}
