using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using GameShift.Core.System;
using Microsoft.Win32.SafeHandles;
using Serilog;

namespace GameShift.Core.Updates;

/// <summary>
/// Updates an installed copy by running the release installer. The installer is downloaded into a new
/// folder under &lt;install dir&gt;\updates, which inherits the install folder's admin-only ACL (Program
/// Files), so nothing a standard user writes can take its place. Nothing else is written: the expected
/// hash stays in memory, and the staged file is hashed again right before it runs. Folders are created
/// and removed through handles that never follow a junction or link, so a reparse point in the updates
/// folder is refused instead of being written or deleted through.
/// </summary>
public static class InstallerUpdate
{
    /// <summary>Folder under the install directory that holds staged installers.</summary>
    public const string UpdatesFolderName = "updates";

    /// <summary>
    /// Installer command line: no wizard, no message boxes, no reboot, and close GameShift if it is
    /// still running. /RELAUNCH=1 asks the installer to start GameShift again when it is done; other
    /// silent installs leave it closed.
    /// </summary>
    public const string InstallerArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1";

    private const string Sha256Prefix = "sha256:";

    private const uint FolderAccess =
        NativeInterop.FILE_TRAVERSE | NativeInterop.FILE_LIST_DIRECTORY | NativeInterop.FILE_READ_ATTRIBUTES | NativeInterop.SYNCHRONIZE;
    private const uint ShareReadWrite = NativeInterop.FILE_SHARE_READ | NativeInterop.FILE_SHARE_WRITE;
    private const uint ShareAll = ShareReadWrite | NativeInterop.FILE_SHARE_DELETE;
    private const uint EntryOptions = NativeInterop.FILE_SYNCHRONOUS_IO_NONALERT | NativeInterop.FILE_OPEN_REPARSE_POINT;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    /// <summary>&lt;install dir&gt;\updates</summary>
    public static string GetUpdatesRoot(string installDirectory) =>
        Path.Combine(installDirectory, UpdatesFolderName);

    /// <summary>
    /// Creates a new, randomly named folder under &lt;install dir&gt;\updates and returns the path the
    /// installer should be downloaded to inside it. Throws when the install folder or its updates
    /// folder is a reparse point or cannot be opened.
    /// </summary>
    public static string CreateStagingPath(string installDirectory)
    {
        var folderName = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var root = GetUpdatesRoot(installDirectory);

        using var install = OpenPlainFolder(installDirectory, FolderAccess, ShareAll, out var refusal, out _)
            ?? throw new IOException($"Refusing to stage an update in {installDirectory}: {refusal}");
        using var updates = OpenPlainEntry(install, UpdatesFolderName, FolderAccess, ShareReadWrite,
                                NativeInterop.FILE_OPEN_IF, NativeInterop.FILE_DIRECTORY_FILE, out _, out refusal)
            ?? throw new IOException($"Refusing to stage an update in {root}: {refusal}");
        using var staging = OpenPlainEntry(updates, folderName, FolderAccess, ShareReadWrite,
                                NativeInterop.FILE_CREATE, NativeInterop.FILE_DIRECTORY_FILE, out _, out refusal)
            ?? throw new IOException($"Could not create a staging folder in {root}: {refusal}");

        return Path.Combine(root, folderName, UpdateAssetSelector.InstallerAssetName);
    }

    /// <summary>
    /// Verifies the staged installer against <paramref name="expectedSha256"/> and starts it with
    /// <see cref="InstallerArguments"/>. The caller MUST shut the app down when this returns true, so the
    /// installer can replace its files. Returns false, without starting anything, if the path is not a
    /// staged installer of this install, the digest is missing, or the hash does not match.
    /// </summary>
    public static bool Launch(string installerPath, string installDirectory, string? expectedSha256) =>
        Launch(installerPath, installDirectory, expectedSha256, StartProcess);

    internal static bool Launch(
        string installerPath,
        string installDirectory,
        string? expectedSha256,
        Func<ProcessStartInfo, bool> startProcess)
    {
        try
        {
            if (!IsStagedInstallerPath(installerPath, installDirectory))
            {
                Log.Error("InstallerUpdate: Refusing to run {Path}: not a staged installer under {Root}",
                    installerPath, GetUpdatesRoot(installDirectory));
                return false;
            }

            if (!TryParseSha256(expectedSha256, out var expectedHex))
            {
                Log.Error("InstallerUpdate: No valid SHA-256 digest for the staged installer - refusing to run it");
                DiscardStaged(installerPath, installDirectory);
                return false;
            }

            bool hashMatched;
            bool started = false;

            // Hold the file open without write or delete sharing from the hash until the process has
            // started, so the bytes that run are the bytes that were hashed.
            using (var stream = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var actualHex = Convert.ToHexString(SHA256.HashData(stream));
                hashMatched = actualHex.Equals(expectedHex, StringComparison.OrdinalIgnoreCase);

                if (!hashMatched)
                {
                    Log.Error("InstallerUpdate: Staged installer hash mismatch (expected {Expected}, got {Actual}) - refusing to run it",
                        expectedHex, actualHex);
                }
                else
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = installerPath,
                        Arguments = InstallerArguments,
                        WorkingDirectory = Path.GetDirectoryName(installerPath)!,
                        UseShellExecute = false
                    };
                    started = startProcess(psi);
                }
            }

            if (!hashMatched)
            {
                DiscardStaged(installerPath, installDirectory);
                return false;
            }

            if (!started)
            {
                Log.Error("InstallerUpdate: Could not start the installer at {Path}", installerPath);
                return false;
            }

            Log.Information("InstallerUpdate: Installer started from {Path}, app will now exit", installerPath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "InstallerUpdate: Failed to verify or start the staged installer");
            return false;
        }
    }

    /// <summary>
    /// Deletes the staging folder of <paramref name="installerPath"/> (after a failed download or a
    /// refused launch). Does nothing unless the path is a staged installer of this install. Best effort.
    /// </summary>
    public static void DiscardStaged(string installerPath, string installDirectory)
    {
        try
        {
            if (!IsStagedInstallerPath(installerPath, installDirectory)) return;

            var stagingDir = Path.GetDirectoryName(Path.GetFullPath(installerPath))!;
            var root = Path.GetDirectoryName(stagingDir)!;
            using var updates = OpenPlainFolder(root, FolderAccess, ShareReadWrite, out var refusal, out var missing);
            if (updates == null)
            {
                if (!missing)
                    Log.Warning("InstallerUpdate: Not deleting the staging folder of {Path}: {Root} {Reason}", installerPath, root, refusal);
                return;
            }

            RemoveStagedEntry(updates, root, Path.GetFileName(stagingDir));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "InstallerUpdate: Could not delete staging folder of {Path}", installerPath);
        }
    }

    /// <summary>
    /// Removes &lt;install dir&gt;\updates left by an earlier update. Call at startup. A folder that is
    /// still in use (the installer relaunches GameShift before it exits) is left for the next start.
    /// </summary>
    public static void CleanupStaging(string installDirectory)
    {
        var root = GetUpdatesRoot(installDirectory);
        try
        {
            using var updates = OpenPlainFolder(root, FolderAccess | NativeInterop.DELETE, ShareReadWrite, out var refusal, out var missing);
            if (updates == null)
            {
                if (!missing)
                    Log.Warning("InstallerUpdate: Not cleaning up {Root}: {Reason}", root, refusal);
                return;
            }

            // Names come from the path, but every removal opens the name relative to the handle checked
            // above, which cannot be renamed or replaced while it is open.
            foreach (var entry in Directory.EnumerateFileSystemEntries(root))
                RemoveStagedEntry(updates, root, Path.GetFileName(entry));

            // Refused while anything is left; the folder then stays for the next start.
            var status = HandleRelative.MarkForDeletion(updates);
            if (status == 0)
                Log.Debug("InstallerUpdate: Cleaned up {Root}", root);
            else
                Log.Debug("InstallerUpdate: {Root} not removed yet (status 0x{Status:X8})", root, status);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "InstallerUpdate: Cleanup of {Root} incomplete (non-fatal)", root);
        }
    }

    /// <summary>
    /// Removes one entry of the updates folder: a staging folder with the files in it, or a stray file.
    /// A reparse point, or a folder inside a staging folder, is left in place and logged. Best effort:
    /// a file still in use (an installer that is running) stays for the next start.
    /// </summary>
    private static void RemoveStagedEntry(SafeFileHandle updates, string root, string name)
    {
        var path = Path.Combine(root, name);
        using var entry = OpenPlainEntry(updates, name, FolderAccess | NativeInterop.DELETE, ShareReadWrite,
            NativeInterop.FILE_OPEN, 0, out var isDirectory, out var refusal);
        if (entry == null)
        {
            Log.Warning("InstallerUpdate: Left {Path} in place: {Reason}", path, refusal);
            return;
        }

        if (isDirectory)
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
            {
                using var file = OpenPlainEntry(entry, Path.GetFileName(child),
                    NativeInterop.DELETE | NativeInterop.FILE_READ_ATTRIBUTES | NativeInterop.SYNCHRONIZE, ShareAll,
                    NativeInterop.FILE_OPEN, 0, out var childIsDirectory, out refusal);
                if (file == null || childIsDirectory)
                {
                    Log.Warning("InstallerUpdate: Left {Path} in place: {Reason}", child, refusal ?? "it is a folder");
                    continue;
                }

                MarkForDeletion(file, child);
            }
        }

        MarkForDeletion(entry, path);
    }

    private static void MarkForDeletion(SafeFileHandle handle, string path)
    {
        var status = HandleRelative.MarkForDeletion(handle);
        if (status != 0)
            Log.Debug("InstallerUpdate: Could not remove {Path} yet (status 0x{Status:X8})", path, status);
    }

    /// <summary>
    /// Opens a folder by path. The handle names the folder itself, never what a reparse point at that
    /// name points to. Null with the reason when it is a reparse point, not a folder, or cannot be
    /// opened; <paramref name="missing"/> is true when it does not exist.
    /// </summary>
    private static SafeFileHandle? OpenPlainFolder(string path, uint access, uint share, out string? refusal, out bool missing)
    {
        missing = false;
        var handle = NativeInterop.CreateFile(path, access, share, IntPtr.Zero, NativeInterop.OPEN_EXISTING,
            NativeInterop.FILE_FLAG_BACKUP_SEMANTICS | NativeInterop.FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            missing = error is ErrorFileNotFound or ErrorPathNotFound;
            refusal = $"it could not be opened (error {error})";
            handle.Dispose();
            return null;
        }

        return CheckPlain(handle, requireDirectory: true, out _, out refusal);
    }

    /// <summary>Opens <paramref name="name"/> relative to <paramref name="folder"/>, with the same refusals.</summary>
    private static SafeFileHandle? OpenPlainEntry(SafeFileHandle folder, string name, uint access, uint share,
        uint disposition, uint options, out bool isDirectory, out string? refusal)
    {
        isDirectory = false;
        var handle = HandleRelative.Open(folder, name, access, share, disposition, options | EntryOptions, out var status);
        if (handle == null)
        {
            refusal = $"it could not be opened (status 0x{status:X8})";
            return null;
        }

        return CheckPlain(handle, requireDirectory: (options & NativeInterop.FILE_DIRECTORY_FILE) != 0, out isDirectory, out refusal);
    }

    private static SafeFileHandle? CheckPlain(SafeFileHandle handle, bool requireDirectory, out bool isDirectory, out string? refusal)
    {
        isDirectory = false;
        if (!NativeInterop.GetFileInformationByHandle(handle, out var info))
            refusal = $"its attributes could not be read (error {Marshal.GetLastWin32Error()})";
        else if ((info.FileAttributes & NativeInterop.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
            refusal = "it is a reparse point";
        else if (requireDirectory && (info.FileAttributes & NativeInterop.FILE_ATTRIBUTE_DIRECTORY) == 0)
            refusal = "it is not a folder";
        else
        {
            isDirectory = (info.FileAttributes & NativeInterop.FILE_ATTRIBUTE_DIRECTORY) != 0;
            refusal = null;
            return handle;
        }

        handle.Dispose();
        return null;
    }

    /// <summary>
    /// True only for &lt;install dir&gt;\updates\&lt;one folder&gt;\GameShift-Setup.exe.
    /// </summary>
    internal static bool IsStagedInstallerPath(string installerPath, string installDirectory)
    {
        var installDir = InstallationDetector.NormalizeDirectory(installDirectory);
        if (installDir == null || string.IsNullOrWhiteSpace(installerPath) ||
            !Path.IsPathFullyQualified(installerPath))
            return false;

        var fullPath = Path.GetFullPath(installerPath);
        if (!Path.GetFileName(fullPath).Equals(UpdateAssetSelector.InstallerAssetName, StringComparison.OrdinalIgnoreCase))
            return false;

        var stagingDir = Path.GetDirectoryName(fullPath);
        var updatesRoot = stagingDir == null ? null : Path.GetDirectoryName(stagingDir);
        return updatesRoot != null &&
               string.Equals(updatesRoot, GetUpdatesRoot(installDir), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parses "sha256:&lt;64 hex chars&gt;" as reported by the GitHub releases API.</summary>
    internal static bool TryParseSha256(string? digest, out string hex)
    {
        hex = "";
        if (string.IsNullOrWhiteSpace(digest) ||
            !digest.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var candidate = digest[Sha256Prefix.Length..].Trim();
        if (candidate.Length != 64 || !IsHex(candidate))
            return false;

        hex = candidate;
        return true;
    }

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }
        return true;
    }

    private static bool StartProcess(ProcessStartInfo psi)
    {
        using var process = Process.Start(psi);
        return process != null;
    }
}
