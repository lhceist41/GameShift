using System.Security.Principal;
using System.Text.Json;
using GameShift.Core.System;
using Serilog;

namespace GameShift.Core.SystemTweaks.Tweaks;

/// <summary>
/// Grants the SeLockMemoryPrivilege (Lock Pages in Memory) to the current user through the
/// LSA policy API. This enables games that support Large Pages (2MB pages instead of 4KB)
/// to reduce TLB misses.
///
/// Games that benefit:
///   - Unreal Engine 5 games (automatic when privilege is available)
///   - Minecraft Java Edition (with -XX:+UseLargePages JVM flag)
///   - Source 2 games (Deadlock, CS2) may benefit with -largepages launch option
///
/// NOT included in "Apply All Recommended" - opt-in only.
/// Requires logoff/reboot to take effect.
/// </summary>
public class EnableLargePages : ISystemTweak
{
    public string Name => "Enable Large Pages Privilege";
    public string Description => "Grants the Lock Pages in Memory privilege, enabling games that support large pages (2MB) to reduce TLB misses and improve memory access performance. Requires logoff or reboot.";
    public string Category => "Memory";
    public bool RequiresReboot => true; // Logoff suffices, but reboot is safer messaging

    private const string LockMemoryRight = "SeLockMemoryPrivilege";
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private readonly IAccountRights _rights;
    private readonly SecurityIdentifier? _user;

    public EnableLargePages() : this(new LsaAccountRights(), GetCurrentUserSid()) { }

    internal EnableLargePages(IAccountRights rights, SecurityIdentifier? user)
    {
        _rights = rights;
        _user = user;
    }

    public bool DetectIsApplied()
    {
        try
        {
            return HasLockMemoryPrivilege() == true;
        }
        catch
        {
            return false;
        }
    }

    public string? Apply()
    {
        // An unreadable policy must not be recorded as "not granted before", or Revert would later
        // remove a right GameShift did not add.
        bool alreadyHad = HasLockMemoryPrivilege()
            ?? throw new InvalidOperationException("Failed to read SeLockMemoryPrivilege holders");
        if (alreadyHad)
        {
            // Record that the privilege pre-existed so Revert leaves it alone - instead of returning
            // null, which the manager would store as a stale applied-with-no-originals entry.
            Log.Information("[LargePages] SeLockMemoryPrivilege already granted");
            return JsonSerializer.Serialize(new { WasGranted = true });
        }

        if (!GrantLockMemoryPrivilege())
        {
            // Throw so the manager records a failure rather than a stale applied-state with no originals.
            throw new InvalidOperationException("Failed to grant SeLockMemoryPrivilege");
        }

        Log.Information("[LargePages] SeLockMemoryPrivilege granted - logoff/reboot required");
        return JsonSerializer.Serialize(new { WasGranted = false });
    }

    public bool Revert(string? originalValuesJson)
    {
        if (string.IsNullOrEmpty(originalValuesJson)) return false;

        try
        {
            var doc = JsonDocument.Parse(originalValuesJson);
            bool wasGranted = doc.RootElement.GetProperty("WasGranted").GetBoolean();

            if (wasGranted)
            {
                // Was already granted before GameShift - nothing to revert
                return true;
            }

            if (!RevokeLockMemoryPrivilege())
            {
                return false;
            }

            Log.Information("[LargePages] SeLockMemoryPrivilege revoked - logoff/reboot required");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[LargePages] Failed to revert");
            return false;
        }
    }

    /// <summary>
    /// Whether the current user or the Administrators group holds SeLockMemoryPrivilege directly;
    /// null when the policy could not be read.
    /// </summary>
    private bool? HasLockMemoryPrivilege()
    {
        bool? user = HoldsLockMemoryRight(_user);
        bool? admins = HoldsLockMemoryRight(Administrators);
        if (user == true || admins == true) return true;
        if (user == null || admins == null) return null;
        return false;
    }

    private bool? HoldsLockMemoryRight(SecurityIdentifier? account)
    {
        if (account == null) return false;

        int status = _rights.Enumerate(account, out var rights);
        // An account object that does not exist holds no rights.
        if (status == NativeInterop.STATUS_OBJECT_NAME_NOT_FOUND) return false;
        if (status != NativeInterop.STATUS_SUCCESS)
        {
            LogFailure("read the rights of", account, status);
            return null;
        }

        return rights.Contains(LockMemoryRight, StringComparer.OrdinalIgnoreCase);
    }

    private bool GrantLockMemoryPrivilege()
    {
        if (_user == null)
        {
            Log.Warning("[LargePages] Current user has no SID, cannot grant SeLockMemoryPrivilege");
            return false;
        }

        int status = _rights.Add(_user, LockMemoryRight);
        if (status == NativeInterop.STATUS_SUCCESS) return true;

        LogFailure("grant SeLockMemoryPrivilege to", _user, status);
        return false;
    }

    private bool RevokeLockMemoryPrivilege()
    {
        if (_user == null)
        {
            Log.Warning("[LargePages] Current user has no SID, cannot revoke SeLockMemoryPrivilege");
            return false;
        }

        // allRights: false removes only this right; true would strip every right the account holds.
        int status = _rights.Remove(_user, allRights: false, LockMemoryRight);
        if (status is NativeInterop.STATUS_SUCCESS or NativeInterop.STATUS_OBJECT_NAME_NOT_FOUND) return true;

        LogFailure("revoke SeLockMemoryPrivilege from", _user, status);
        return false;
    }

    private static void LogFailure(string action, SecurityIdentifier account, int status)
    {
        Log.Warning("[LargePages] Failed to {Action} {Sid}: NTSTATUS 0x{Status:X8} (Win32 error {Error})",
            action, account.Value, status, NativeInterop.LsaNtStatusToWinError(status));
    }

    private static SecurityIdentifier? GetCurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User;
    }
}
