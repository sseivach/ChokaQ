using ChokaQ.Abstractions.DTOs;
using Microsoft.AspNetCore.Components;

namespace ChokaQ.TheDeck.UI.Components.Health;

public partial class Health
{
    [Parameter] public SystemHealthDto? HealthSnapshot { get; set; }

    private IReadOnlyList<DlqErrorGroupDto> TopErrors =>
        HealthSnapshot?.TopErrors ?? Array.Empty<DlqErrorGroupDto>();

    private static string FormatAge(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;

        if (age.TotalSeconds < 60)
            return $"{Math.Max(0, (int)age.TotalSeconds)}s";

        if (age.TotalMinutes < 60)
            return $"{(int)age.TotalMinutes}m";

        return $"{(int)age.TotalHours}h";
    }

    private static string TrimPrefix(string prefix)
    {
        const int maxLength = 88;
        return prefix.Length <= maxLength
            ? prefix
            : prefix[..maxLength] + "...";
    }
}
