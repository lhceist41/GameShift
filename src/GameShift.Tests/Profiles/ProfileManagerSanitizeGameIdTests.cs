using GameShift.Core.Profiles;
using Xunit;

namespace GameShift.Tests.Profiles;

/// <summary>
/// Tests for <see cref="ProfileManager.SanitizeGameId"/> which protects against
/// path traversal, invalid filename characters, empty results, and Windows reserved names.
/// </summary>
public class ProfileManagerSanitizeGameIdTests
{
    // ── Valid IDs pass through unchanged ──────────────────────────────

    [Theory]
    [InlineData("steam_12345", "steam_12345")]
    [InlineData("valorant", "valorant")]
    [InlineData("Overwatch2", "Overwatch2")]
    [InlineData("epic_Fortnite", "epic_Fortnite")]
    [InlineData("gog_1234567890", "gog_1234567890")]
    public void ValidIds_PassThroughUnchanged(string input, string expected)
    {
        Assert.Equal(expected, ProfileManager.SanitizeGameId(input));
    }

    // ── Path traversal attempts ───────────────────────────────────────

    [Theory]
    [InlineData("..\\..\\Windows\\System32\\evil", "__Windows_System32_evil")]
    [InlineData("../../etc/passwd", "__etc_passwd")]
    [InlineData("game/../evil", "game__evil")]
    [InlineData("..\\game", "_game")]
    [InlineData("game\\..", "game_")]
    public void PathTraversalAttempts_StrippedSafely(string input, string expected)
    {
        var result = ProfileManager.SanitizeGameId(input);

        Assert.Equal(expected, result);
        Assert.DoesNotContain("..", result);
        Assert.DoesNotContain("/", result);
        Assert.DoesNotContain("\\", result);
    }

    // ── Windows reserved device names ─────────────────────────────────

    [Theory]
    [InlineData("CON")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("COM2")]
    [InlineData("COM3")]
    [InlineData("COM4")]
    [InlineData("COM5")]
    [InlineData("COM6")]
    [InlineData("COM7")]
    [InlineData("COM8")]
    [InlineData("COM9")]
    [InlineData("LPT1")]
    [InlineData("LPT2")]
    [InlineData("LPT3")]
    [InlineData("LPT4")]
    [InlineData("LPT5")]
    [InlineData("LPT6")]
    [InlineData("LPT7")]
    [InlineData("LPT8")]
    [InlineData("LPT9")]
    [InlineData("con")]   // lowercase should also be handled case-insensitively
    [InlineData("Prn")]   // mixed case should also be handled
    [InlineData("nul")]
    [InlineData("com1")]
    [InlineData("lpt9")]
    public void WindowsReservedNames_Prefixed(string input)
    {
        var result = ProfileManager.SanitizeGameId(input);

        // Result should NOT equal the reserved name (case-insensitively)
        Assert.NotEqual(input.ToUpperInvariant(), result.ToUpperInvariant());
        // Result should start with underscore (the guard prefix)
        Assert.StartsWith("_", result);
        // Original text should still be preserved after the prefix
        Assert.Equal("_" + input, result);
    }

    [Theory]
    [InlineData("CONSOLE", "CONSOLE")]   // not exactly reserved, passes through
    [InlineData("COMPUTER", "COMPUTER")]
    [InlineData("COM10", "COM10")]       // COM10 is NOT a reserved name, only COM1-COM9
    [InlineData("LPT0", "LPT0")]         // LPT0 is NOT reserved, only LPT1-LPT9
    [InlineData("LPT10", "LPT10")]
    public void NonReservedLookalikes_PassThrough(string input, string expected)
    {
        Assert.Equal(expected, ProfileManager.SanitizeGameId(input));
    }

    // ── Empty or fully-stripped input returns safe default ────────────

    [Theory]
    [InlineData("..")]
    [InlineData("/")]
    [InlineData("\\")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void EmptyOrAllStrippedInput_ReturnsSafeDefault(string input)
    {
        var result = ProfileManager.SanitizeGameId(input);

        Assert.False(string.IsNullOrWhiteSpace(result));
        // Should be a usable filename
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            Assert.DoesNotContain(c, result);
        }
    }

    [Theory]
    [InlineData("..", "unknown")]
    [InlineData("", "unknown")]
    [InlineData("   ", "unknown")]
    [InlineData("....", "unknown")]  // "..", "..", "" all stripped → empty → "unknown"
    public void FullyStrippedInput_ReturnsUnknownSentinel(string input, string expected)
    {
        Assert.Equal(expected, ProfileManager.SanitizeGameId(input));
    }

    // ── Invalid filename characters are stripped ──────────────────────

    [Theory]
    [InlineData("game:name", "game_name")]     // colon is invalid on Windows
    [InlineData("game|pipe", "game_pipe")]
    [InlineData("game<lt>", "game_lt_")]
    [InlineData("game\"quote\"", "game_quote_")]
    [InlineData("game?ask", "game_ask")]
    [InlineData("game*star", "game_star")]
    public void InvalidFilenameChars_Stripped(string input, string expected)
    {
        var result = ProfileManager.SanitizeGameId(input);

        Assert.Equal(expected, result);
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            Assert.DoesNotContain(c, result);
        }
    }

    // ── Combined / edge cases ─────────────────────────────────────────

    [Fact]
    public void ComplexInput_CombinesAllSanitizationSteps()
    {
        // Contains: path traversal, forward slash, backslash, invalid char, and ends with result that
        // should not match a reserved name.
        var result = ProfileManager.SanitizeGameId("../game:name\\..\\evil");

        Assert.DoesNotContain("..", result);
        Assert.DoesNotContain("/", result);
        Assert.DoesNotContain("\\", result);
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            Assert.DoesNotContain(c, result);
        }
        Assert.False(string.IsNullOrWhiteSpace(result));
    }

    [Fact]
    public void ReservedNameWithInvalidChars_SanitizedAndPrefixedIfStillReserved()
    {
        // "CON:" → strip ":" → "CON_" → NOT exactly reserved → passes through as "CON_"
        var result = ProfileManager.SanitizeGameId("CON:");
        Assert.Equal("CON_", result);
    }

    [Fact]
    public void SanitizedResult_UsableAsFilename()
    {
        // A realistic hostile input should still produce a path-combinable result.
        var hostile = "..\\..\\..\\Windows\\System32\\drivers\\etc\\hosts:alternate";
        var result = ProfileManager.SanitizeGameId(hostile);

        // Must be combinable with a directory into a valid path — no exception.
        var combined = Path.Combine(Path.GetTempPath(), $"{result}.json");
        Assert.NotNull(combined);
        // Result of GetFileName should match the sanitized id + extension, proving no path
        // components leaked into the final path.
        Assert.Equal($"{result}.json", Path.GetFileName(combined));
    }

    [Fact]
    public void LongValidId_PassesThroughUnchanged()
    {
        var longId = new string('a', 200);
        Assert.Equal(longId, ProfileManager.SanitizeGameId(longId));
    }
}
