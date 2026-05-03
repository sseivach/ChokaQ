namespace ChokaQ.TheDeck;

public class ChokaQTheDeckOptions
{
    public string RoutePrefix { get; set; } = "/chokaq";

    /// <summary>
    /// The name of the ASP.NET Core Authorization Policy to apply to the Dashboard and Hub.
    /// If null, The Deck uses the host application's default authorization policy.
    /// </summary>
    /// <remarks>
    /// The Deck is an administrative control plane: it can cancel, purge, edit, pause, and
    /// resurrect jobs. The safe default is therefore "authenticated users only" via the host
    /// default policy. Use AllowAnonymousDeck only for local demos or intentionally public sandboxes.
    /// When DestructiveAuthorizationPolicy is set, this policy becomes the read/connect policy.
    /// </remarks>
    public string? AuthorizationPolicy { get; set; }

    /// <summary>
    /// Optional policy required for write/destructive Hub commands.
    /// </summary>
    /// <remarks>
    /// This enables a production split between operators who may inspect The Deck and operators
    /// who may mutate jobs or queues. Leave null to use the same authorization boundary as the
    /// dashboard connection policy.
    /// </remarks>
    public string? DestructiveAuthorizationPolicy { get; set; }

    /// <summary>
    /// Explicitly allows The Deck UI and SignalR Hub to be served without authorization.
    /// Default: false.
    /// </summary>
    public bool AllowAnonymousDeck { get; set; } = false;

    /// <summary>
    /// Queue lag threshold where the dashboard should warn operators about saturation.
    /// </summary>
    /// <remarks>
    /// Lag thresholds are UI policy, not storage policy. Storage reports measured reality;
    /// The Deck decides how aggressively to color that reality for humans on call.
    /// </remarks>
    public double QueueLagWarningThresholdSeconds { get; set; } = 5;

    /// <summary>
    /// Queue lag threshold where the dashboard should mark a queue as critical.
    /// </summary>
    /// <remarks>
    /// Keeping the critical threshold configurable matters because "bad" lag is workload-specific.
    /// A payroll batch queue and a user-facing email queue can have very different SLOs.
    /// </remarks>
    public double QueueLagCriticalThresholdSeconds { get; set; } = 10;
}
