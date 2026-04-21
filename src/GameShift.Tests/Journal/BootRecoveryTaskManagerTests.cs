using GameShift.Core.Journal;
using System.Xml;
using Xunit;

namespace GameShift.Tests.Journal;

/// <summary>
/// Tests for <see cref="BootRecoveryTaskManager"/>'s task XML generation.
/// Does not exercise the schtasks invocation — that requires admin privileges and
/// would mutate real system state.
/// </summary>
public class BootRecoveryTaskManagerTests
{
    [Fact]
    public void BuildTaskXml_ProducesValidXml()
    {
        var xml = BootRecoveryTaskManager.BuildTaskXml(@"C:\Program Files\GameShift\Watchdog.exe");

        // Should parse as valid XML
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        Assert.NotNull(doc.DocumentElement);
    }

    [Fact]
    public void BuildTaskXml_EscapesSpecialCharacters()
    {
        // Path containing XML special characters
        var path = @"C:\Program Files\Game&Shift<test>\Watchdog.exe";
        var xml = BootRecoveryTaskManager.BuildTaskXml(path);

        // Should not contain raw & < > - should be escaped
        Assert.DoesNotContain("&Shift<test>", xml);

        // Should parse as valid XML
        var doc = new XmlDocument();
        doc.LoadXml(xml);

        // The escaped path should be readable via XML parsing
        var cmdNode = doc.GetElementsByTagName("Command")[0];
        Assert.NotNull(cmdNode);
        // The XmlNode.InnerText returns the UN-escaped value
        Assert.Equal(path, cmdNode!.InnerText);
    }

    [Fact]
    public void BuildTaskXml_IncludesSystemSidUserContext()
    {
        var xml = BootRecoveryTaskManager.BuildTaskXml(@"C:\gameshift.exe");
        Assert.Contains("S-1-5-18", xml); // LocalSystem SID
    }

    [Fact]
    public void BuildTaskXml_SetsBootTrigger()
    {
        var xml = BootRecoveryTaskManager.BuildTaskXml(@"C:\gameshift.exe");
        Assert.Contains("<BootTrigger", xml);
    }

    [Fact]
    public void BuildTaskXml_IncludesExecutablePath()
    {
        var path = @"C:\Test Path\gameshift.exe";
        var xml = BootRecoveryTaskManager.BuildTaskXml(path);
        Assert.Contains(path, xml);
    }

    [Fact]
    public void BuildTaskXml_IncludesBootRecoveryArgument()
    {
        var xml = BootRecoveryTaskManager.BuildTaskXml(@"C:\gameshift.exe");
        Assert.Contains("--boot-recovery", xml);
    }

    [Fact]
    public void BuildTaskXml_UsesHighestAvailableRunLevel()
    {
        var xml = BootRecoveryTaskManager.BuildTaskXml(@"C:\gameshift.exe");
        Assert.Contains("HighestAvailable", xml);
    }

    [Fact]
    public void BuildTaskXml_UsesTaskSchedulerNamespace()
    {
        var xml = BootRecoveryTaskManager.BuildTaskXml(@"C:\gameshift.exe");
        var doc = new XmlDocument();
        doc.LoadXml(xml);

        Assert.NotNull(doc.DocumentElement);
        Assert.Equal("Task", doc.DocumentElement!.LocalName);
        Assert.Equal("http://schemas.microsoft.com/windows/2004/02/mit/task", doc.DocumentElement.NamespaceURI);
    }
}
