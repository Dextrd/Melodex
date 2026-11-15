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
        (ImportOutcome.Unsure, "to review"),
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
        ImportOutcome.Unsure or ImportOutcome.NotFound when line.InReview => "In Review",
        ImportOutcome.Unsure => "To review",
        ImportOutcome.NotFound => "Couldn't match",
        ImportOutcome.Dropped => "Left out",
        _ => "Matching",
    };

    public static string BadgeClass(ImportLineInfo line) => line.Outcome switch
    {
        ImportOutcome.Unsure or ImportOutcome.NotFound when line.InReview => "partial",
        ImportOutcome.Owned => "owned",
        ImportOutcome.Found => "missing",
        ImportOutcome.Unsure => "partial",
        ImportOutcome.NotFound => "ignored",
        _ => "neutral",
    };

    /// <summary>
    /// The request applying the import made for a song or album, to show how it's getting on: the newest for it since the
    /// import was applied.
    /// </summary>
    public static RequestInfo? Request(IEnumerable<RequestInfo> requests, ImportSummary import, ImportLineInfo line)
    {
        if (import.AppliedAt is not { } applied || line.Outcome != ImportOutcome.Found)
            return null;
        var (kind, target) = import.Albums ? (RequestKind.Album, line.Match?.AlbumId) : (RequestKind.Track, line.Match?.TrackId);
        return target is { } id
            ? requests.FirstOrDefault(r => r.Kind == kind && r.TargetId == id && !r.ListenNow && r.CreatedAt >= applied.AddMinutes(-1))
            : null;
    }
}
