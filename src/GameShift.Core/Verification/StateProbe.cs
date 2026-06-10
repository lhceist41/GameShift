namespace GameShift.Core.Verification;

/// <summary>
/// A flat, comparable snapshot of system state. Every probed item is a key/value string pair
/// (e.g. <c>reg:HKLM\...\Dwm\OverlayTestMode</c> = <c>dword:5</c>), which makes diffing trivial
/// and renders identically across runs. Items that cannot be read are captured with a stable
/// sentinel value so an access problem present in both snapshots never shows up as a difference.
/// </summary>
public sealed class StateProbe
{
    /// <summary>Probed items: stable key -> rendered value.</summary>
    public Dictionary<string, string> Items { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Non-fatal capture problems (access denied, tool timeout), for the report.</summary>
    public List<string> Warnings { get; } = new();

    public int Count => Items.Count;
}

/// <summary>One difference between two probes. Absent items render as <c>&lt;absent&gt;</c>.</summary>
public sealed record StateDifference(string Key, string Before, string After);

/// <summary>
/// Compares two <see cref="StateProbe"/> snapshots. Pure and deterministic so the comparison
/// logic itself is unit-testable without touching any real system state.
/// </summary>
public static class ProbeComparison
{
    public const string Absent = "<absent>";

    public static List<StateDifference> Compare(StateProbe before, StateProbe after)
    {
        var differences = new List<StateDifference>();
        var keys = new HashSet<string>(before.Items.Keys, StringComparer.OrdinalIgnoreCase);
        keys.UnionWith(after.Items.Keys);

        foreach (var key in keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var b = before.Items.TryGetValue(key, out var bv) ? bv : Absent;
            var a = after.Items.TryGetValue(key, out var av) ? av : Absent;
            if (!string.Equals(b, a, StringComparison.Ordinal))
                differences.Add(new StateDifference(key, b, a));
        }

        return differences;
    }
}
