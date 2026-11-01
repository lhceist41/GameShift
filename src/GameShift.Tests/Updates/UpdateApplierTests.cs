using System.IO;
using GameShift.Core.Updates;
using GameShift.Tests.TestHelpers;
using Xunit;

namespace GameShift.Tests.Updates;

/// <summary>
/// A 3.9.0 portable copy still updates itself in place, leaving the ".update" file, its hash sidecar
/// and the update script next to the exe; the next start removes exactly those.
/// </summary>
public class UpdateApplierTests
{
    [Fact]
    public void CleanupPreviousUpdate_RemovesTheThreeLeftovers_AndNothingElse()
    {
        using var temp = new TempPath();
        var exe = temp.GetFile("GameShift.App.exe");
        File.WriteAllText(exe, "exe");
        File.WriteAllText(exe + ".update", "staged");
        File.WriteAllText(exe + ".update.sha256", "hash");
        File.WriteAllText(temp.GetFile("gameshift-update-0123456789abcdef0123456789abcdef.cmd"), "script");
        File.WriteAllText(temp.GetFile("settings.json"), "keep");
        File.WriteAllText(temp.GetFile("other-update.cmd"), "keep");

        UpdateApplier.CleanupPreviousUpdate(() => exe);

        Assert.Equal(
            new[] { "GameShift.App.exe", "other-update.cmd", "settings.json" },
            Directory.GetFiles(temp.Path).Select(Path.GetFileName).Order().ToArray());
    }

    [Fact]
    public void CleanupPreviousUpdate_ExePathUnavailable_DoesNotThrow()
    {
        UpdateApplier.CleanupPreviousUpdate(() => throw new System.InvalidOperationException("no path"));
    }
}
