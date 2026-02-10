namespace ChokaQ.Storage.SqlServer.DataEngine;

/// <summary>
/// SAFE SQL Builder for dynamic ORDER BY clauses.
/// Prevents SQL Injection by whitelisting column names.
/// </summary>
internal static class SqlSortBuilder
{
    public static string BuildOrderBy(string sortBy, bool descending, bool isArchive)
    {
        var direction = descending ? "DESC" : "ASC";
        var column = ValidateColumn(sortBy, isArchive);

        return $"ORDER BY {column} {direction}";
    }

    private static string ValidateColumn(string input, bool isArchive)
    {
        // Normalize input
        var key = input?.Trim().ToLowerInvariant();

        return key switch
        {
            "id" => "[Id]",
            "queue" => "[Queue]",
            "type" => "[Type]",
            "priority" => isArchive ? "0" : "[Priority]", // Archive has no priority usually, but hot/dlq logic varies
            "created" => "[CreatedAtUtc]",
            "worker" => "[WorkerId]",

            // Context specific
            "date" => isArchive ? "[FinishedAtUtc]" : "[FailedAtUtc]",
            "duration" => isArchive ? "[DurationMs]" : "0", // DLQ has no duration
            "reason" => isArchive ? "''" : "[FailureReason]",
            "attempts" => "[AttemptCount]",

            // Default fallback (Safety net)
            _ => isArchive ? "[FinishedAtUtc]" : "[FailedAtUtc]"
        };
    }
}