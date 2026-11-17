using System.Globalization;

namespace GameShift.Core.Presentation;

/// <summary>One summary chip on the During games page, such as "3 applied".</summary>
public sealed record ModuleSummaryChip(ModuleStatusKind StatusKind, int Count, string Text);

/// <summary>The summary chips above the During games list: how many modules show each badge.</summary>
public static class ModuleSummary
{
    // Chip order on the page. One and many read the same ("1 failed", "2 failed"), so there is no plural form.
    private static readonly (ModuleStatusKind Kind, string Word)[] Order =
    [
        (ModuleStatusKind.Applied, "applied"),
        (ModuleStatusKind.Failed, "failed"),
        (ModuleStatusKind.Off, "off"),
        (ModuleStatusKind.NotAvailable, "not available"),
        (ModuleStatusKind.Ready, "ready"),
        (ModuleStatusKind.Paused, "paused"),
    ];

    /// <summary>
    /// One chip per badge kind that at least one module shows, in page order; none for an empty list.
    /// A value outside <see cref="ModuleStatusKind"/> is not counted.
    /// </summary>
    public static IReadOnlyList<ModuleSummaryChip> Chips(IEnumerable<ModuleStatusKind> kinds)
    {
        var counts = new Dictionary<ModuleStatusKind, int>();
        foreach (var kind in kinds)
            counts[kind] = counts.GetValueOrDefault(kind) + 1;

        var chips = new List<ModuleSummaryChip>();
        foreach (var (kind, word) in Order)
        {
            if (counts.TryGetValue(kind, out int count))
                chips.Add(new(kind, count, count.ToString(CultureInfo.InvariantCulture) + " " + word));
        }
        return chips;
    }
}
