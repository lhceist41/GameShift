using System.Globalization;
using GameShift.Core.SystemTweaks;

namespace GameShift.Core.Presentation;

/// <summary>One yes or no question of Restore All, and the changes a yes undoes.</summary>
/// <param name="SeparatelyConfirmed">The changes are ones GameShift did not record, confirmed on their own.</param>
public sealed record RestoreAllQuestion(string Text, IReadOnlyList<LastingChange> Items, bool SeparatelyConfirmed);

/// <summary>What Restore All asks, in order, for the lasting changes found.</summary>
/// <param name="NothingToUndo">Shown instead of any question when nothing was found; null otherwise.</param>
public sealed record RestoreAllPlan(
    string? NothingToUndo, bool NothingToUndoIsWarning, IReadOnlyList<RestoreAllQuestion> Questions);

public static class RestoreAllPrompts
{
    public const string Title = "Restore All";

    internal const string RefusedFolderWarning =
        "GameShift's settings folder could not be used, so the changes it recorded could not be read.";

    /// <summary>The sources that could not be read, the refused settings folder first.</summary>
    public static IReadOnlyList<string> Warnings(LastingChangesList list, bool dataFolderRefused)
    {
        var lines = list.Warnings.ToList();
        if (dataFolderRefused)
            lines.Insert(0, RefusedFolderWarning);
        return lines;
    }

    /// <summary>
    /// The recorded changes are asked about first, then each kind of change GameShift did not
    /// record. The warnings go into the first question, whichever it is, so every path shows them.
    /// </summary>
    public static RestoreAllPlan Plan(LastingChangesList list, bool dataFolderRefused)
    {
        var warningLines = Warnings(list, dataFolderRefused);
        var warnings = warningLines.Count > 0 ? "\n\n" + string.Join("\n", warningLines) : "";

        if (list.Items.Count == 0)
        {
            return warningLines.Count > 0
                ? new("GameShift could not read all of its records, so it cannot tell whether lasting changes are left." + warnings, true, [])
                : new("GameShift found no lasting changes to undo.", false, []);
        }

        var questions = new List<RestoreAllQuestion>();
        var recorded = list.Recorded;
        if (recorded.Count > 0)
        {
            questions.Add(new(
                "GameShift will undo these changes it recorded:\n\n" +
                LastingChangesInventory.DescribeItems(recorded) +
                "\nGameShift does not restart your PC; changes that need a restart finish after you restart." +
                warnings + "\n\nUndo these changes?",
                recorded, false));
        }

        foreach (var group in list.NeedingOwnConfirmation.GroupBy(i => i.Kind))
        {
            var intro = group.Key == LastingChangeKind.CpuCoreReservation
                ? "A CPU core reservation is set on this PC. Earlier versions of GameShift " +
                  "could set one, but so can other tools, and GameShift does not record who set it.\n\n"
                : "These startup apps are turned off by a \"~\" in front of their name. GameShift turns " +
                  "startup apps off this way, but other tools can too, and GameShift does not record " +
                  "which ones it turned off.\n\n";
            var question = group.Key == LastingChangeKind.CpuCoreReservation
                ? "Remove the reservation?"
                : "Turn these startup apps back on?";
            var groupWarnings = questions.Count == 0 && warningLines.Count > 0
                ? "\n" + string.Join("\n", warningLines) + "\n"
                : "";

            questions.Add(new(
                intro + LastingChangesInventory.DescribeItems(group) + groupWarnings + "\n" + question,
                group.ToList(), true));
        }

        return new(null, false, questions);
    }

    /// <summary>The page's status line after an undo.</summary>
    public static string Status(IReadOnlyList<LastingChangeResult> results)
    {
        int undone = results.Count(r => r.Outcome == LastingChangeOutcome.Undone);
        int failed = results.Count(r => r.Outcome == LastingChangeOutcome.Failed);
        return failed > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{undone} lasting changes undone, {failed} failed.")
            : string.Create(CultureInfo.InvariantCulture, $"{undone} lasting changes undone.");
    }
}
