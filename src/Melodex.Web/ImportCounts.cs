using Melodex.Client;

namespace Melodex.Web;

/// <summary>How an import's songs came out, in words.</summary>
public static class ImportCounts
{
    /// <summary>Each outcome as it's named in filters and summaries, in the order they're shown.</summary>
    public static readonly IReadOnlyList<(ImportOutcome Outcome, string Label)> Outcomes =
    [
        (ImportOutcome.Owned, "in your library"),
        (ImportOutcome.Found, "to get"),
        (ImportOutcome.Unsure, "to check"),
        (ImportOutcome.NotFound, "not found"),
        (ImportOutcome.Dropped, "left out"),
    ];

    /// <summary>"12 in your library, 3 to get, 1 not found".</summary>
    public static string Summary(ImportSummary import)
    {
        var parts = Outcomes.Where(o => import.Counts.GetValueOrDefault(o.Outcome) > 0)
            .Select(o => $"{import.Counts[o.Outcome]:N0} {o.Label}").ToList();
        if (import.Counts.GetValueOrDefault(ImportOutcome.Pending) is > 0 and var pending)
            parts.Add($"{pending:N0} still to match");
        return string.Join(", ", parts);
    }
}
