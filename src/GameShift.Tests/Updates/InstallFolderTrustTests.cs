using System.IO;
using System.Linq;
using System.Security.AccessControl;
using GameShift.Core.Journal;
using GameShift.Core.Updates;
using GameShift.Tests.Config;
using GameShift.Tests.TestHelpers;
using Xunit;

namespace GameShift.Tests.Updates;

/// <summary>
/// An install counts as admin-only only when no standard user can change its folder or exe, or move
/// a folder above it out of the way. Descriptors are built from SDDL; the ACLs below were read from a
/// real Windows 11 install (C:\, C:\Program Files, a folder an installer created in it).
/// </summary>
public class InstallFolderTrustTests
{
    private const string TI = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    private const string AUser = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    private const string DriveRootSddl =
        "O:" + TI + "D:PAI(A;;LC;;;AU)(A;OICIIO;SDGXGWGR;;;AU)(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)";

    private const string ProgramFilesSddl =
        "O:" + TI + "D:PAI(A;OICIIO;GA;;;CO)(A;OICIIO;GA;;;SY)(A;;0x1301bf;;;SY)(A;OICIIO;GA;;;BA)(A;;0x1301bf;;;BA)" +
        "(A;OICIIO;GXGR;;;BU)(A;;0x1200a9;;;BU)(A;CIIO;GA;;;" + TI + ")(A;;FA;;;" + TI + ")(A;;0x1200a9;;;AC)" +
        "(A;OICIIO;GXGR;;;AC)(A;;0x1200a9;;;S-1-15-2-2)(A;OICIIO;GXGR;;;S-1-15-2-2)";

    // Inherited ACEs of a folder an elevated installer created in Program Files.
    private const string InstallFolderDacl =
        "D:AI(A;ID;FA;;;" + TI + ")(A;CIIOID;GA;;;" + TI + ")(A;ID;FA;;;SY)(A;OICIIOID;GA;;;SY)(A;ID;FA;;;BA)" +
        "(A;OICIIOID;GA;;;BA)(A;ID;0x1200a9;;;BU)(A;OICIIOID;GXGR;;;BU)(A;OICIIOID;GA;;;CO)(A;ID;0x1200a9;;;AC)" +
        "(A;OICIIOID;GXGR;;;AC)(A;ID;0x1200a9;;;S-1-15-2-2)(A;OICIIOID;GXGR;;;S-1-15-2-2)";

    private const string ExeDacl =
        "D:AI(A;ID;FA;;;SY)(A;ID;FA;;;BA)(A;ID;0x1200a9;;;BU)(A;ID;0x1200a9;;;AC)(A;ID;0x1200a9;;;S-1-15-2-2)";

    internal static JournalObjectSecurity FromSddl(
        string sddl, bool isDirectory = true, bool isReparsePoint = false, int linkCount = 1)
    {
        var descriptor = new RawSecurityDescriptor(sddl);
        var dacl = descriptor.DiscretionaryAcl?.Cast<GenericAce>().Select(ace =>
        {
            var known = (QualifiedAce)ace;
            var type = known.AceQualifier == AceQualifier.AccessDenied ? JournalAceType.Deny : JournalAceType.Allow;
            return new JournalAce(known.SecurityIdentifier.Value, type, known.AccessMask, (ace.AceFlags & AceFlags.InheritOnly) != 0);
        }).ToList();
        return new JournalObjectSecurity(descriptor.Owner?.Value, dacl, DaclProtected: false, isDirectory, isReparsePoint, linkCount);
    }

    private static JournalObjectSecurity Folder(string owner = "BA", string dacl = InstallFolderDacl) =>
        FromSddl($"O:{owner}{dacl}");

    private static JournalObjectSecurity Exe(string owner = "BA", string dacl = ExeDacl, int links = 1) =>
        FromSddl($"O:{owner}{dacl}", isDirectory: false, linkCount: links);

    private static InstallFolderDescriptor Install(
        JournalObjectSecurity? folder = null, JournalObjectSecurity? exe = null, params JournalObjectSecurity[] ancestors) =>
        new(folder ?? Folder(), exe ?? Exe(),
            ancestors.Length > 0 ? ancestors : new[] { FromSddl(ProgramFilesSddl), FromSddl(DriveRootSddl) });

    [Fact]
    public void RealProgramFilesInstall_IsTrusted()
    {
        var result = InstallFolderTrust.Evaluate(Install());

        Assert.True(result.Trusted, string.Join("; ", result.Reasons));
    }

    [Fact]
    public void ExeOwnedByTrustedInstaller_IsTrusted()
    {
        Assert.True(InstallFolderTrust.Evaluate(Install(exe: Exe(owner: TI))).Trusted);
    }

    [Fact]
    public void FolderOwnedByAUser_IsNotTrusted()
    {
        // The user's non-elevated token keeps the user SID, so CREATOR OWNER full control is theirs too.
        var result = InstallFolderTrust.Evaluate(Install(folder: Folder(owner: AUser)));

        Assert.False(result.Trusted);
        Assert.Contains(result.Reasons, r => r.Contains("install folder is owned by " + AUser));
        Assert.Contains(result.Reasons, r => r.Contains("resolved to " + AUser));
    }

    [Fact]
    public void ExeOwnedByAUser_IsNotTrusted_AlsoThroughTheFoldersCreatorOwnerEntry()
    {
        var result = InstallFolderTrust.Evaluate(Install(exe: Exe(owner: AUser)));

        Assert.False(result.Trusted);
        Assert.Contains(result.Reasons, r => r.Contains("exe is owned by " + AUser));
        Assert.Contains(result.Reasons, r => r.StartsWith("the install folder") && r.Contains("resolved to " + AUser));
    }

    [Fact]
    public void FolderCreatedUnderTheDriveRoot_IsNotTrusted()
    {
        // What a folder made directly under C:\ inherits: Authenticated Users may modify it.
        var underRoot = Folder(dacl: "D:AI(A;ID;0x1301bf;;;AU)(A;OICIIOID;SDGXGWGR;;;AU)(A;OICIID;FA;;;SY)(A;OICIID;FA;;;BA)(A;OICIID;0x1200a9;;;BU)");

        var result = InstallFolderTrust.Evaluate(Install(underRoot, null, FromSddl(DriveRootSddl)));

        Assert.False(result.Trusted);
        Assert.Contains(result.Reasons, r => r.Contains("S-1-5-11"));
    }

    [Theory]
    [InlineData("(A;;GW;;;WD)")]          // Everyone, generic write
    [InlineData("(A;;0x2;;;IU)")]         // INTERACTIVE, FILE_WRITE_DATA
    [InlineData("(A;;0x4;;;BU)")]         // Users, add subfolder
    [InlineData("(A;;0x40;;;BU)")]        // Users, delete child
    [InlineData("(A;;WD;;;AU)")]          // Authenticated Users, WRITE_DAC
    [InlineData("(A;;GA;;;BU)")]          // Users, generic all
    [InlineData("(A;OICIIO;GW;;;BU)")]    // inherit-only: decides what new files in the folder allow
    public void FolderGrantingAWriteRightToAnyoneElse_IsNotTrusted(string ace)
    {
        var result = InstallFolderTrust.Evaluate(Install(folder: Folder(dacl: InstallFolderDacl + ace)));

        Assert.False(result.Trusted);
    }

    [Theory]
    [InlineData("(A;;FA;;;BU)")]
    [InlineData("(A;;GW;;;" + AUser + ")")]
    [InlineData("(A;;0x100;;;WD)")]       // FILE_WRITE_ATTRIBUTES
    public void ExeGrantingAWriteRightToAnyoneElse_IsNotTrusted(string ace)
    {
        Assert.False(InstallFolderTrust.Evaluate(Install(exe: Exe(dacl: ExeDacl + ace))).Trusted);
    }

    [Fact]
    public void DenyEntries_AreIgnored()
    {
        var result = InstallFolderTrust.Evaluate(Install(folder: Folder(dacl: InstallFolderDacl + "(D;;FA;;;WD)")));

        Assert.True(result.Trusted, string.Join("; ", result.Reasons));
    }

    [Fact]
    public void FolderThatIsAReparsePoint_IsNotTrusted()
    {
        var junction = FromSddl("O:BA" + InstallFolderDacl, isReparsePoint: true);

        Assert.Contains(InstallFolderTrust.Evaluate(Install(folder: junction)).Reasons, r => r.Contains("reparse point"));
    }

    [Fact]
    public void ExeThatIsALinkOrHasMoreThanOneName_IsNotTrusted()
    {
        Assert.False(InstallFolderTrust.Evaluate(Install(exe: FromSddl("O:BA" + ExeDacl, isDirectory: false, isReparsePoint: true))).Trusted);
        Assert.Contains(InstallFolderTrust.Evaluate(Install(exe: Exe(links: 2))).Reasons, r => r.Contains("2 links"));
    }

    [Fact]
    public void WrongKindOfEntry_IsNotTrusted()
    {
        Assert.False(InstallFolderTrust.Evaluate(Install(folder: FromSddl("O:BA" + InstallFolderDacl, isDirectory: false))).Trusted);
        Assert.False(InstallFolderTrust.Evaluate(Install(exe: FromSddl("O:BA" + ExeDacl, isDirectory: true))).Trusted);
    }

    [Fact]
    public void NoDacl_IsNotTrusted()
    {
        var noDacl = new JournalObjectSecurity("S-1-5-32-544", null, false, IsDirectory: true, IsReparsePoint: false, LinkCount: 1);

        Assert.Contains(InstallFolderTrust.Evaluate(Install(folder: noDacl)).Reasons, r => r.Contains("no DACL"));
    }

    [Theory]
    [InlineData("(A;;0x40;;;BU)")]        // Users may delete or rename the folder below
    [InlineData("(A;;SD;;;AU)")]          // Authenticated Users may delete or rename this folder
    [InlineData("(A;;WD;;;WD)")]          // Everyone may change the DACL
    [InlineData("(A;;GA;;;BU)")]
    public void ParentThatLetsAnyoneElseMoveTheInstall_IsNotTrusted(string ace)
    {
        var parent = FromSddl(ProgramFilesSddl + ace);

        var result = InstallFolderTrust.Evaluate(Install(null, null, parent, FromSddl(DriveRootSddl)));

        Assert.False(result.Trusted);
        Assert.Contains(result.Reasons, r => r.Contains("folder 1 above the install"));
    }

    [Fact]
    public void AncestorFurtherUpThatLetsAnyoneElseMoveIt_IsNotTrusted()
    {
        var root = FromSddl(DriveRootSddl + "(A;;0x40;;;AU)");

        var result = InstallFolderTrust.Evaluate(Install(null, null, FromSddl(ProgramFilesSddl), root));

        Assert.Contains(result.Reasons, r => r.Contains("folder 2 above the install"));
    }

    [Fact]
    public void AncestorOwnedByAUser_OrAReparsePoint_IsNotTrusted()
    {
        var userOwned = FromSddl(ProgramFilesSddl.Replace("O:" + TI, "O:" + AUser));
        var junction = FromSddl(ProgramFilesSddl, isReparsePoint: true);

        Assert.Contains(InstallFolderTrust.Evaluate(Install(null, null, userOwned, FromSddl(DriveRootSddl))).Reasons,
            r => r.Contains("folder 1 above the install is owned by " + AUser));
        Assert.Contains(InstallFolderTrust.Evaluate(Install(null, null, junction, FromSddl(DriveRootSddl))).Reasons,
            r => r.Contains("folder 1 above the install is a reparse point"));
    }

    [Fact]
    public void Check_MissingExe_IsNotTrusted_AndDoesNotThrow()
    {
        using var temp = new TempPath();

        var result = InstallFolderTrust.Check(Path.Combine(temp.Path, "missing", "GameShift.App.exe"));

        Assert.False(result.Trusted);
    }

    [Fact]
    public void Check_ReadsTheFolderItself_NeverWhatAJunctionPointsTo()
    {
        using var temp = new TempPath();
        var real = Path.Combine(temp.Path, "real");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "GameShift.App.exe"), "exe");
        var link = Path.Combine(temp.Path, "GameShift");
        using var junction = UserDataFolderTests.Junction(link, real);

        var result = InstallFolderTrust.Check(Path.Combine(link, "GameShift.App.exe"));

        Assert.False(result.Trusted);
        Assert.Contains(result.Reasons, r => r.Contains("install folder is a reparse point"));
    }

    [Fact]
    public void Read_DescribesEveryFolderUpToTheDriveRoot()
    {
        using var temp = new TempPath();
        var exe = temp.GetFile("GameShift.App.exe");
        File.WriteAllText(exe, "exe");

        var descriptor = InstallFolderTrust.Read(exe);

        var depth = temp.Path.TrimEnd('\\').Count(c => c == '\\');
        Assert.Equal(depth, descriptor.Ancestors.Count);
        Assert.True(descriptor.Folder.IsDirectory);
        Assert.False(descriptor.Exe.IsDirectory);
        Assert.All(descriptor.Ancestors, a => Assert.True(a.IsDirectory));
    }
}
