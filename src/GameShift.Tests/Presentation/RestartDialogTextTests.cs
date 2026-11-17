using System.Text.RegularExpressions;
using GameShift.Core.Presentation;
using Xunit;

namespace GameShift.Tests.Presentation;

/// <summary>
/// The restart dialog shows the prompt's own text in three rows: nothing is added, nothing is
/// dropped, and only the sentences about the restart itself move to the Restart row.
/// </summary>
public class RestartDialogTextTests
{
    public static TheoryData<string, RestartPrompt> Prompts => new()
    {
        { "turn off memory integrity", RestartPrompts.TurnOffMemoryIntegrity(null) },
        { "turn off memory integrity with dependents", RestartPrompts.TurnOffMemoryIntegrity("Hyper-V and WSL2 use the hypervisor.") },
        { "turn on memory integrity", RestartPrompts.TurnOnMemoryIntegrity() },
        { "fix amd timer", RestartPrompts.FixAmdTimer() },
        { "finish latency fixes", RestartPrompts.FinishLatencyFixes() },
        { "apply a system tweak", RestartPrompts.ChangeSystemTweak("HAGS", undo: false, warning: null) },
        { "apply a system tweak with a warning", RestartPrompts.ChangeSystemTweak("HAGS", undo: false, RestartPrompts.HagsFrameGenerationWarning(true, true)) },
        { "undo a system tweak", RestartPrompts.ChangeSystemTweak("HAGS", undo: true, warning: null) },
    };

    private static string[] Sentences(string text) =>
        Regex.Split(text.Trim(), @"(?<=[.!?])\s+").Where(s => s.Length > 0).Order(StringComparer.Ordinal).ToArray();

    [Theory]
    [MemberData(nameof(Prompts))]
    public void Rows_KeepEveryWordOfThePromptAndAddNone(string action, RestartPrompt prompt)
    {
        var rows = RestartDialogText.For(prompt);

        var shown = Sentences(rows.WhatChanges).Concat(Sentences(rows.Restart)).Order(StringComparer.Ordinal);
        Assert.True(Sentences(prompt.WhatChanges).SequenceEqual(shown), action);
        Assert.Equal(prompt.HowToUndo, rows.HowToUndo);
        Assert.Equal(prompt.Command, rows.Command);
    }

    [Theory]
    [MemberData(nameof(Prompts))]
    public void EveryRestartPrompt_FillsTheRestartRow(string action, RestartPrompt prompt)
    {
        var rows = RestartDialogText.For(prompt);

        Assert.True(rows.ShowsRestart, action);
        Assert.Contains("restart", rows.Restart, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AmdTimer_SplitsTheChangeFromWhenItTakesEffect()
    {
        var rows = RestartDialogText.For(RestartPrompts.FixAmdTimer());

        Assert.StartsWith("GameShift removes the platform-timer settings", rows.WhatChanges);
        Assert.DoesNotContain("restart", rows.WhatChanges, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("This takes effect after a restart. Restart now restarts Windows 30 seconds after you confirm.", rows.Restart);
    }

    [Fact]
    public void WhatStopsWorkingUntilARestart_StaysUnderWhatChanges()
    {
        var rows = RestartDialogText.For(RestartPrompts.TurnOffMemoryIntegrity("Hyper-V and WSL2 use the hypervisor."));

        Assert.Contains("They stop working until you turn Memory Integrity back on and restart.", rows.WhatChanges);
        Assert.Equal("Windows restarts 30 seconds after you confirm.", rows.Restart);
    }

    [Fact]
    public void TweakWarning_StaysUnderWhatChanges()
    {
        var rows = RestartDialogText.For(RestartPrompts.ChangeSystemTweak(
            "HAGS", undo: false, RestartPrompts.HagsFrameGenerationWarning(true, true)));

        Assert.Contains("Frame Generation", rows.WhatChanges);
        Assert.DoesNotContain("Frame Generation", rows.Restart);
    }

    [Fact]
    public void PromptThatIsOnlyARestart_HidesWhatChanges()
    {
        var prompt = RestartPrompts.FinishLatencyFixes();
        var rows = RestartDialogText.For(prompt);

        Assert.False(rows.ShowsWhatChanges);
        Assert.Equal(prompt.WhatChanges, rows.Restart);
    }

    [Fact]
    public void Command_ShowsWhatChangesEvenWithoutOtherText()
    {
        var prompt = new RestartPrompt("Title", "Windows restarts 10 seconds after you confirm.",
            "bcdedit /deletevalue useplatformclock", "Undo text.", "Restart now", null);
        var rows = RestartDialogText.For(prompt);

        Assert.True(rows.ShowsWhatChanges);
        Assert.Equal("", rows.WhatChanges);
        Assert.Equal("bcdedit /deletevalue useplatformclock", rows.Command);
    }

    [Fact]
    public void PromptWithoutARestartSentence_KeepsItsTextWholeAndHidesTheRestartRow()
    {
        var prompt = new RestartPrompt("Title", "GameShift changes one setting. It needs a restart later.",
            null, "Undo text.", "Apply and restart now", null);
        var rows = RestartDialogText.For(prompt);

        Assert.False(rows.ShowsRestart);
        Assert.Equal(prompt.WhatChanges, rows.WhatChanges);
    }
}
