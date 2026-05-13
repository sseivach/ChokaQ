namespace ChokaQ.Abstractions.Observability;

/// <summary>
/// Configures the OpenTelemetry metric surface emitted by ChokaQ.
/// </summary>
/// <remarks>
/// Metrics are a public production contract. Names and units should be stable, but tag values
/// must be bounded. A single tenant, queue, exception message, or dynamically generated job type
/// should not be able to create an unbounded number of Prometheus time series.
/// </remarks>
public sealed class ChokaQMetricsOptions
{
    /// <summary>
    /// Maximum number of distinct queue tag values emitted by this process before overflow.
    /// Default: 100.
    /// </summary>
    public int MaxQueueTagValues { get; set; } = 100;

    /// <summary>
    /// Maximum number of distinct job-type tag values emitted by this process before overflow.
    /// Default: 500.
    /// </summary>
    public int MaxJobTypeTagValues { get; set; } = 500;

    /// <summary>
    /// Maximum number of distinct exception-type tag values emitted by this process before overflow.
    /// Default: 100.
    /// </summary>
    public int MaxErrorTagValues { get; set; } = 100;

    /// <summary>
    /// Maximum number of distinct DLQ failure-reason tag values emitted by this process before overflow.
    /// Default: 50.
    /// </summary>
    public int MaxFailureReasonTagValues { get; set; } = 50;

    /// <summary>
    /// Maximum length of a tag value after trimming. Default: 128 characters.
    /// </summary>
    public int MaxTagValueLength { get; set; } = 128;

    /// <summary>
    /// Stable value used when ChokaQ receives a blank metric tag. Default: "unknown".
    /// </summary>
    public string UnknownTagValue { get; set; } = "unknown";

    /// <summary>
    /// Stable value used after a tag key reaches its cardinality budget. Default: "other".
    /// </summary>
    public string OverflowTagValue { get; set; } = "other";

    /// <summary>
    /// Throws a startup exception when metric cardinality settings are unsafe or ambiguous.
    /// </summary>
    public void ValidateOrThrow()
    {
        var errors = Validate();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Invalid ChokaQ metrics configuration: " + string.Join("; ", errors));
        }
    }

    /// <summary>
    /// Returns all validation errors for diagnostics and startup validation.
    /// </summary>
    public IReadOnlyList<string> Validate(string prefix = "Metrics")
    {
        var errors = new List<string>();

        RequirePositive(MaxQueueTagValues, $"{prefix}.MaxQueueTagValues", errors);
        RequirePositive(MaxJobTypeTagValues, $"{prefix}.MaxJobTypeTagValues", errors);
        RequirePositive(MaxErrorTagValues, $"{prefix}.MaxErrorTagValues", errors);
        RequirePositive(MaxFailureReasonTagValues, $"{prefix}.MaxFailureReasonTagValues", errors);
        RequirePositive(MaxTagValueLength, $"{prefix}.MaxTagValueLength", errors);

        if (string.IsNullOrWhiteSpace(UnknownTagValue))
        {
            errors.Add($"{prefix}.UnknownTagValue must not be empty.");
        }
        else if (UnknownTagValue.Trim().Length > MaxTagValueLength)
        {
            errors.Add($"{prefix}.UnknownTagValue must not be longer than {prefix}.MaxTagValueLength.");
        }

        if (string.IsNullOrWhiteSpace(OverflowTagValue))
        {
            errors.Add($"{prefix}.OverflowTagValue must not be empty.");
        }
        else if (OverflowTagValue.Trim().Length > MaxTagValueLength)
        {
            errors.Add($"{prefix}.OverflowTagValue must not be longer than {prefix}.MaxTagValueLength.");
        }

        return errors;
    }

    private static void RequirePositive(int value, string name, ICollection<string> errors)
    {
        if (value <= 0)
        {
            errors.Add($"{name} must be greater than zero.");
        }
    }
}
