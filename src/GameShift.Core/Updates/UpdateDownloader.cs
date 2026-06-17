using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace GameShift.Core.Updates;

/// <summary>
/// Downloads a release asset from GitHub with streaming progress reporting.
/// Downloads to a .tmp file first, then renames on success to avoid partial files.
/// </summary>
public static class UpdateDownloader
{
    private static readonly HttpClient _downloadClient = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    static UpdateDownloader()
    {
        _downloadClient.DefaultRequestHeaders.Add("User-Agent", "GameShift-UpdateDownloader");
    }

    /// <summary>
    /// Downloads a file from the given URL to the target path with progress reporting.
    /// Downloads to targetPath + ".tmp" first, then renames on success.
    /// </summary>
    /// <param name="downloadUrl">Direct download URL (GitHub asset browser_download_url)</param>
    /// <param name="targetPath">Final file path for the downloaded file</param>
    /// <param name="expectedSize">Expected file size in bytes (0 if unknown)</param>
    /// <param name="expectedSha256">Expected content digest ("sha256:&lt;hex&gt;") from the release API.
    /// The download is verified against this before being promoted; a null/blank or non-sha256 value is rejected.</param>
    /// <param name="progress">Progress reporter: 0.0 to 1.0</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if the download completed AND was verified successfully</returns>
    public static async Task<bool> DownloadAsync(
        string downloadUrl,
        string targetPath,
        long expectedSize,
        string? expectedSha256,
        IProgress<double> progress,
        CancellationToken ct)
    {
        if (!GitHubUrlValidator.IsValid(downloadUrl))
        {
            Log.Warning("UpdateDownloader: Rejected download URL outside allowed domains: {Url}", downloadUrl);
            return false;
        }

        var tmpPath = targetPath + ".tmp";

        try
        {
            Log.Information("UpdateDownloader: Starting download from {Url}", downloadUrl);

            using var response = await _downloadClient.GetAsync(
                downloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                ct);

            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? expectedSize;

            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(
                tmpPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                hasher.AppendData(buffer, 0, bytesRead);
                totalRead += bytesRead;

                if (totalBytes > 0)
                {
                    progress.Report((double)totalRead / totalBytes);
                }
            }

            await fileStream.FlushAsync(ct);
            fileStream.Close();

            // Integrity gate: this binary is moved over the running ELEVATED executable by
            // UpdateApplier, so an unverified download is a path to SYSTEM-level code execution.
            // Refuse to promote the file unless its size and SHA-256 match what the release
            // metadata advertised - fail closed, never apply an update we cannot verify.
            if (totalBytes > 0 && totalRead != totalBytes)
            {
                Log.Error("UpdateDownloader: Size mismatch (expected {Expected:N0}, got {Actual:N0}) - discarding",
                    totalBytes, totalRead);
                CleanupTmp(tmpPath);
                return false;
            }

            if (string.IsNullOrWhiteSpace(expectedSha256) ||
                !expectedSha256.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                Log.Error("UpdateDownloader: No SHA-256 digest available for this asset - refusing to apply an unverified update");
                CleanupTmp(tmpPath);
                return false;
            }

            var expectedHex = expectedSha256["sha256:".Length..].Trim();
            var actualHex = Convert.ToHexString(hasher.GetHashAndReset());
            if (!actualHex.Equals(expectedHex, StringComparison.OrdinalIgnoreCase))
            {
                Log.Error("UpdateDownloader: SHA-256 mismatch (expected {Expected}, got {Actual}) - discarding tampered/corrupt download",
                    expectedHex, actualHex);
                CleanupTmp(tmpPath);
                return false;
            }

            // Atomic rename: delete target if exists, then move verified tmp into place
            if (File.Exists(targetPath))
                File.Delete(targetPath);

            File.Move(tmpPath, targetPath);

            // Write a sidecar with the verified hash so the apply step can RE-verify the staged file
            // before moving it over the running elevated exe. The staging directory may be
            // user-writable, so the in-session check alone does not protect a later apply.
            try { File.WriteAllText(targetPath + ".sha256", actualHex); }
            catch (Exception ex) { Log.Warning(ex, "UpdateDownloader: Could not write hash sidecar"); }

            Log.Information("UpdateDownloader: Complete and verified, {Bytes:N0} bytes (sha256 {Hash})",
                totalRead, actualHex);
            progress.Report(1.0);
            return true;
        }
        catch (OperationCanceledException)
        {
            Log.Information("UpdateDownloader: Download cancelled");
            CleanupTmp(tmpPath);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UpdateDownloader: Download failed");
            CleanupTmp(tmpPath);
            return false;
        }
    }

    private static void CleanupTmp(string tmpPath)
    {
        try
        {
            if (File.Exists(tmpPath))
                File.Delete(tmpPath);
        }
        catch { }
    }
}
