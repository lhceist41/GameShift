using GameShift.Core.SystemTweaks;
using Xunit;

namespace GameShift.Tests.SystemTweaks;

/// <summary>
/// Reading the BCD values from <c>bcdedit /enum {current}</c> output does not depend on the display
/// language: the German listing gives the same values as the English one.
/// </summary>
public class KernelTuningParseTests
{
    // Captured on a German Windows 11 install with disabledynamictick and tscsyncpolicy set; the
    // recovery and resume GUIDs are replaced with neutral ones.
    private static readonly string GermanBothSet = string.Join("\r\n",
        "",
        "Windows-Startladeprogramm",
        "-------------------------",
        "Bezeichner              {current}",
        "device                  partition=C:",
        @"path                    \WINDOWS\system32\winload.efi",
        "description             Windows 11",
        "locale                  de-DE",
        "inherit                 {bootloadersettings}",
        "recoverysequence        {00000000-0000-0000-0000-000000000001}",
        "displaymessageoverride  Recovery",
        "recoveryenabled         Yes",
        "isolatedcontext         Yes",
        "allowedinmemorysettings 0x15000075",
        "osdevice                partition=C:",
        @"systemroot              \WINDOWS",
        "resumeobject            {00000000-0000-0000-0000-000000000002}",
        "nx                      OptIn",
        "tscsyncpolicy           Enhanced",
        "bootmenupolicy          Standard",
        "disabledynamictick      Yes",
        "");

    // The same install before either value was set.
    private static readonly string GermanNoneSet = string.Join("\r\n",
        "",
        "Windows-Startladeprogramm",
        "-------------------------",
        "Bezeichner              {current}",
        "device                  partition=C:",
        @"path                    \WINDOWS\system32\winload.efi",
        "description             Windows 11",
        "locale                  de-DE",
        "inherit                 {bootloadersettings}",
        "recoverysequence        {00000000-0000-0000-0000-000000000001}",
        "displaymessageoverride  Recovery",
        "recoveryenabled         Yes",
        "isolatedcontext         Yes",
        "allowedinmemorysettings 0x15000075",
        "osdevice                partition=C:",
        @"systemroot              \WINDOWS",
        "resumeobject            {00000000-0000-0000-0000-000000000002}",
        "nx                      OptIn",
        "bootmenupolicy          Standard",
        "");

    private static readonly string EnglishBothSet = string.Join("\r\n",
        "",
        "Windows Boot Loader",
        "-------------------",
        "identifier              {current}",
        "device                  partition=C:",
        @"path                    \WINDOWS\system32\winload.efi",
        "description             Windows 11",
        "locale                  en-US",
        "inherit                 {bootloadersettings}",
        "recoverysequence        {00000000-0000-0000-0000-000000000001}",
        "displaymessageoverride  Recovery",
        "recoveryenabled         Yes",
        "isolatedcontext         Yes",
        "allowedinmemorysettings 0x15000075",
        "osdevice                partition=C:",
        @"systemroot              \WINDOWS",
        "resumeobject            {00000000-0000-0000-0000-000000000002}",
        "nx                      OptIn",
        "tscsyncpolicy           Enhanced",
        "bootmenupolicy          Standard",
        "disabledynamictick      Yes",
        "");

    private static Dictionary<string, string?> Parse(string output)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var setting in KernelTuningManager.AllSettings)
            values[setting.BcdKey] = null;

        KernelTuningManager.ParseEnumOutput(output, values);
        return values;
    }

    [Fact]
    public void GermanListing_ReadsTheSetValues_AndLeavesTheOthersUnset()
    {
        var values = Parse(GermanBothSet);

        Assert.Equal("Yes", values["disabledynamictick"]);
        Assert.Equal("Enhanced", values["tscsyncpolicy"]);
        Assert.Null(values["useplatformtick"]);
        Assert.Null(values["x2apicpolicy"]);
        Assert.Null(values["hypervisorlaunchtype"]);
        Assert.Null(values["useplatformclock"]);
    }

    [Fact]
    public void GermanHeadings_AddNoValues()
    {
        var values = Parse(GermanBothSet);

        Assert.Equal(KernelTuningManager.AllSettings.Length, values.Count);
        Assert.False(values.ContainsKey("Bezeichner"));
        Assert.False(values.ContainsKey("Windows-Startladeprogramm"));
    }

    [Fact]
    public void GermanListing_WithNothingSet_LeavesEveryValueUnset()
    {
        Assert.All(Parse(GermanNoneSet).Values, Assert.Null);
    }

    [Fact]
    public void EnglishListing_GivesTheSameValuesAsTheGermanOne()
    {
        Assert.Equal(Parse(GermanBothSet), Parse(EnglishBothSet));
    }
}
