using Melodex.Client;

namespace Melodex.Web;

/// <summary>How an import's songs came out, in words.</summary>
public static class ImportCounts
{
    /// <summary>Each outcome as it's named in filters and summaries, in the order they're shown.</summary>
    public static readonly IReadOnlyList<(ImportOutcome Outcome, string Label)> Outcomes =
    [
        (ImportOutcome.Owned, "owned"),
        (ImportOutcome.Found, "missing"),
        (ImportOutcome.Unsure, "to check"),
        (ImportOutcome.NotFound, "couldn't match"),
        (ImportOutcome.Dropped, "left out"),
    ];

    /// <summary>"12 owned, 3 missing, 1 couldn't match".</summary>
    public static string Summary(ImportSummary import)
    {
        var parts = Outcomes.Where(o => import.Counts.GetValueOrDefault(o.Outcome) > 0)
            .Select(o => $"{import.Counts[o.Outcome]:N0} {o.Label}").ToList();
        if (import.Counts.GetValueOrDefault(ImportOutcome.Pending) is > 0 and var pending)
            parts.Add($"{pending:N0} still to match");
        return string.Join(", ", parts);
    }

    /// <summary>A song's outcome, as its badge says it.</summary>
    public static string Label(ImportLineInfo line) => line.Outcome switch
    {
        ImportOutcome.Owned => "Owned",
        ImportOutcome.Found => line.Chosen ? "Chosen, missing" : "Missing",
        ImportOutcome.Unsure => "Check it",
        ImportOutcome.NotFound => line.InReview ? "In Review" : "Couldn't match",
        ImportOutcome.Dropped => "Left out",
        _ => "Matching",
    };

    public static string BadgeClass(ImportLineInfo line) => line.Outcome switch
    {
        ImportOutcome.Owned => "owned",
        ImportOutcome.Found => "missing",
        ImportOutcome.Unsure => "partial",
        ImportOutcome.NotFound => "ignored",
        _ => "neutral",
    };
}
