using System.Text.RegularExpressions;

namespace GameShift.Core.Presentation;

/// <summary>
/// The rows of the restart dialog, taken from a <see cref="RestartPrompt"/> without adding text:
/// the sentences that say when Windows restarts or when the change takes effect move to the
/// Restart row, everything else stays under What changes in its original order.
/// </summary>
/// <param name="WhatChanges">Empty when the prompt says nothing beyond the restart.</param>
/// <param name="Restart">Empty when the prompt has no sentence about the restart.</param>
public sealed record RestartDialogText(string WhatChanges, string? Command, string Restart, string HowToUndo)
{
    public const string WhatChangesLabel = "What changes";
    public const string RestartLabel = "Restart";
    public const string HowToUndoLabel = "How to undo";

    // The openings RestartPrompts uses for its restart sentences. A sentence that only mentions a
    // restart in passing (for example what stops working until the next one) stays where it is.
    private static readonly string[] RestartOpenings =
    [
        "Windows restarts ",
        "This takes effect after a restart",
        "Restart now restarts ",
    ];

    private static readonly Regex SentenceEnd = new(@"(?<=[.!?])\s+", RegexOptions.CultureInvariant);

    public bool ShowsWhatChanges => WhatChanges.Length > 0 || !string.IsNullOrEmpty(Command);

    public bool ShowsRestart => Restart.Length > 0;

    public static RestartDialogText For(RestartPrompt prompt)
    {
        var what = new List<string>();
        var restart = new List<string>();
        foreach (var sentence in SentenceEnd.Split(prompt.WhatChanges.Trim()))
        {
            if (sentence.Length == 0)
                continue;
            (IsRestartSentence(sentence) ? restart : what).Add(sentence);
        }

        return new(string.Join(" ", what), prompt.Command, string.Join(" ", restart), prompt.HowToUndo);
    }

    private static bool IsRestartSentence(string sentence) =>
        RestartOpenings.Any(opening => sentence.StartsWith(opening, StringComparison.Ordinal));
}
