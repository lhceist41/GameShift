using GameShift.Core.Presentation;
using Xunit;

namespace GameShift.Tests.Presentation;

/// <summary>
/// Every restart or boot action asks through one prompt: closing it any other way cancels, and
/// "restart later" exists only where the action has its own restart step.
/// </summary>
public class RestartPromptTests
{
    public static TheoryData<string, RestartPrompt, bool> Prompts => new()
    {
        { "turn off memory integrity", RestartPrompts.TurnOffMemoryIntegrity(null), false },
        { "turn on memory integrity", RestartPrompts.TurnOnMemoryIntegrity(), false },
        { "fix amd timer", RestartPrompts.FixAmdTimer(), true },
        { "finish latency fixes", RestartPrompts.FinishLatencyFixes(), false },
        { "apply a system tweak", RestartPrompts.ChangeSystemTweak("HAGS", undo: false, warning: null), true },
        { "undo a system tweak", RestartPrompts.ChangeSystemTweak("HAGS", undo: true, warning: null), true },
    };

    [Theory]
    [MemberData(nameof(Prompts))]
    public void Prompt_OffersLaterOnlyWhereTheRestartIsASeparateStep(string action, RestartPrompt prompt, bool allowsLater)
    {
        Assert.True(allowsLater == prompt.AllowsLater, action);
        Assert.Equal(allowsLater ? RestartChoice.ApplyRestartLater : RestartChoice.Cancel,
            prompt.Choose(RestartDialogButton.Secondary));
    }

    [Theory]
    [MemberData(nameof(Prompts))]
    public void Prompt_SaysWhatChangesAndHowToUndoInPlainText(string action, RestartPrompt prompt, bool allowsLater)
    {
        _ = allowsLater;
        foreach (var text in new[] { prompt.Title, prompt.WhatChanges, prompt.HowToUndo, prompt.PrimaryText })
        {
            Assert.False(string.IsNullOrWhiteSpace(text), action);
            Assert.DoesNotContain('\u2014', text);
            Assert.DoesNotContain('\u2013', text);
        }
    }

    [Theory]
    [MemberData(nameof(Prompts))]
    public void Choose_PrimaryRestartsNowAndAnythingElseCancels(string action, RestartPrompt prompt, bool allowsLater)
    {
        _ = (action, allowsLater);
        Assert.Equal(RestartChoice.ApplyAndRestartNow, prompt.Choose(RestartDialogButton.Primary));
        Assert.Equal(RestartChoice.Cancel, prompt.Choose(RestartDialogButton.None));
    }

    [Fact]
    public void TurnOffMemoryIntegrity_NamesWhatStopsWorking()
    {
        var with = RestartPrompts.TurnOffMemoryIntegrity("Hyper-V and WSL2 use the hypervisor.");
        var without = RestartPrompts.TurnOffMemoryIntegrity(null);

        Assert.Contains("Hyper-V and WSL2 use the hypervisor. They stop working until you turn Memory Integrity back on and restart.",
            with.WhatChanges);
        Assert.DoesNotContain("stop working", without.WhatChanges);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void HagsFrameGenerationWarning_OnlyWhenTurningOffHagsBreaksFrameGeneration(bool capable, bool enabled, bool warns)
    {
        var warning = RestartPrompts.HagsFrameGenerationWarning(capable, enabled);

        Assert.Equal(warns, warning != null);
        var prompt = RestartPrompts.ChangeSystemTweak("HAGS", undo: false, warning);
        Assert.Equal(warns, prompt.WhatChanges.Contains("Frame Generation", StringComparison.Ordinal));
    }

    [Fact]
    public void ChangeSystemTweak_UndoSaysUndoOnItsButtons()
    {
        var undo = RestartPrompts.ChangeSystemTweak("HAGS", undo: true, warning: null);

        Assert.Contains("back to the value it had", undo.WhatChanges);
        Assert.StartsWith("Undo", undo.PrimaryText);
        Assert.StartsWith("Undo", undo.LaterText);
    }
}
