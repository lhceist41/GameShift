using System.Security.Principal;
using GameShift.Core.System;
using GameShift.Core.SystemTweaks.Tweaks;
using Xunit;

namespace GameShift.Tests.SystemTweaks;

/// <summary>
/// Tests for <see cref="EnableLargePages"/> through its account-rights seam, so no user right changes on
/// the test machine. The user SID is fixed so the result does not depend on who runs the tests.
/// </summary>
public class EnableLargePagesTests
{
    private const string Right = "SeLockMemoryPrivilege";
    private const int StatusSuccess = 0;
    private const int StatusAccessDenied = unchecked((int)0xC0000022);
    private const int StatusObjectNameNotFound = unchecked((int)0xC0000034);

    private static readonly SecurityIdentifier User = new("S-1-5-21-1000000001-1000000002-1000000003-1001");
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private sealed class FakeRights : IAccountRights
    {
        public Dictionary<SecurityIdentifier, List<string>> Held { get; } = new();
        public Dictionary<SecurityIdentifier, int> EnumerateStatus { get; } = new();
        public int AddStatus { get; set; } = StatusSuccess;
        public int RemoveStatus { get; set; } = StatusSuccess;
        public List<(SecurityIdentifier Account, string Right)> Adds { get; } = new();
        public List<(SecurityIdentifier Account, bool AllRights, string Right)> Removes { get; } = new();

        public int Enumerate(SecurityIdentifier account, out IReadOnlyList<string> rights)
        {
            rights = Array.Empty<string>();
            if (EnumerateStatus.TryGetValue(account, out int status))
                return status;
            if (!Held.TryGetValue(account, out var held))
                return StatusObjectNameNotFound;
            rights = held;
            return StatusSuccess;
        }

        public int Add(SecurityIdentifier account, string right)
        {
            Adds.Add((account, right));
            return AddStatus;
        }

        public int Remove(SecurityIdentifier account, bool allRights, string right)
        {
            Removes.Add((account, allRights, right));
            return RemoveStatus;
        }
    }

    private static (EnableLargePages Tweak, FakeRights Rights) Create(SecurityIdentifier? user = null)
    {
        var rights = new FakeRights();
        return (new EnableLargePages(rights, user ?? User), rights);
    }

    [Fact]
    public void Detect_UserHoldsRight_True()
    {
        var (tweak, rights) = Create();
        rights.Held[User] = new() { "SeServiceLogonRight", Right };

        Assert.True(tweak.DetectIsApplied());
    }

    [Fact]
    public void Detect_AdministratorsHoldRight_True()
    {
        var (tweak, rights) = Create();
        rights.Held[Administrators] = new() { Right };

        Assert.True(tweak.DetectIsApplied());
    }

    [Fact]
    public void Detect_OtherRightsOnly_False()
    {
        var (tweak, rights) = Create();
        rights.Held[User] = new() { "SeServiceLogonRight" };
        rights.Held[Administrators] = new() { "SeBackupPrivilege" };

        Assert.False(tweak.DetectIsApplied());
    }

    [Fact]
    public void Detect_PolicyUnreadable_False()
    {
        var (tweak, rights) = Create();
        rights.EnumerateStatus[User] = StatusAccessDenied;
        rights.EnumerateStatus[Administrators] = StatusAccessDenied;

        Assert.False(tweak.DetectIsApplied());
    }

    [Fact]
    public void Apply_NotHeld_GrantsOnlyThatRightToTheUserAndRecordsIt()
    {
        var (tweak, rights) = Create();

        var original = tweak.Apply();

        Assert.Equal((User, Right), Assert.Single(rights.Adds));
        Assert.Empty(rights.Removes);
        Assert.Equal("{\"WasGranted\":false}", original);
    }

    [Fact]
    public void Apply_UserAlreadyHolds_RecordsPreExistingWithoutGranting()
    {
        var (tweak, rights) = Create();
        rights.Held[User] = new() { Right };

        Assert.Equal("{\"WasGranted\":true}", tweak.Apply());
        Assert.Empty(rights.Adds);
    }

    [Fact]
    public void Apply_AdministratorsAlreadyHold_RecordsPreExistingWithoutGranting()
    {
        var (tweak, rights) = Create();
        rights.Held[Administrators] = new() { Right };

        Assert.Equal("{\"WasGranted\":true}", tweak.Apply());
        Assert.Empty(rights.Adds);
    }

    [Fact]
    public void Apply_GrantFails_Throws()
    {
        var (tweak, rights) = Create();
        rights.AddStatus = StatusAccessDenied;

        Assert.Throws<InvalidOperationException>(() => tweak.Apply());
        Assert.Single(rights.Adds);
    }

    [Fact]
    public void Apply_UserRightsUnreadable_ThrowsWithoutGranting()
    {
        // A grant on top of an unread state would be recorded as "not held before", and Revert would
        // then remove a right that predates GameShift.
        var (tweak, rights) = Create();
        rights.EnumerateStatus[User] = StatusAccessDenied;

        Assert.Throws<InvalidOperationException>(() => tweak.Apply());
        Assert.Empty(rights.Adds);
    }

    [Fact]
    public void Apply_AdministratorsRightsUnreadable_ThrowsWithoutGranting()
    {
        var (tweak, rights) = Create();
        rights.EnumerateStatus[Administrators] = StatusAccessDenied;

        Assert.Throws<InvalidOperationException>(() => tweak.Apply());
        Assert.Empty(rights.Adds);
    }

    [Fact]
    public void Apply_UserHoldsButAdministratorsUnreadable_RecordsPreExisting()
    {
        var (tweak, rights) = Create();
        rights.Held[User] = new() { Right };
        rights.EnumerateStatus[Administrators] = StatusAccessDenied;

        Assert.Equal("{\"WasGranted\":true}", tweak.Apply());
        Assert.Empty(rights.Adds);
    }

    [Fact]
    public void Revert_GrantedByGameShift_RemovesOnlyThatRightFromTheUser()
    {
        var (tweak, rights) = Create();

        Assert.True(tweak.Revert("{\"WasGranted\":false}"));

        Assert.Equal((User, false, Right), Assert.Single(rights.Removes));
        Assert.Empty(rights.Adds);
    }

    [Fact]
    public void Revert_AccountHasNoRightsLeft_Succeeds()
    {
        var (tweak, rights) = Create();
        rights.RemoveStatus = StatusObjectNameNotFound;

        Assert.True(tweak.Revert("{\"WasGranted\":false}"));
    }

    [Fact]
    public void Revert_RemoveFails_ReportsFailure()
    {
        var (tweak, rights) = Create();
        rights.RemoveStatus = StatusAccessDenied;

        Assert.False(tweak.Revert("{\"WasGranted\":false}"));
    }

    [Fact]
    public void Revert_HeldBeforeGameShift_LeavesTheRight()
    {
        var (tweak, rights) = Create();

        Assert.True(tweak.Revert("{\"WasGranted\":true}"));
        Assert.Empty(rights.Removes);
    }

    [Fact]
    public void NoUserSid_NeitherGrantsNorRevokes()
    {
        var rights = new FakeRights();
        var tweak = new EnableLargePages(rights, user: null);

        Assert.Throws<InvalidOperationException>(() => tweak.Apply());
        Assert.False(tweak.Revert("{\"WasGranted\":false}"));
        Assert.Empty(rights.Adds);
        Assert.Empty(rights.Removes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    public void Revert_NoUsableRecord_ReportsFailureWithoutRemoving(string? original)
    {
        var (tweak, rights) = Create();

        Assert.False(tweak.Revert(original));
        Assert.Empty(rights.Removes);
    }
}
