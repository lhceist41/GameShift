using System;
using System.IO;
using System.Net.Http;
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
    /// <param name="progress">Progress reporter: 0.0 to 1.0</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if download completed successfully</returns>
    public static async Task<bool> DownloadAsync(
        string downloadUrl,
        string targetPath,
        long expectedSize,
        IProgress<double> progress,
        CancellationToken ct)
    {
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

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                totalRead += bytesRead;

                if (totalBytes > 0)
                {
                    progress.Report((double)totalRead / totalBytes);
                }
            }

            await fileStream.FlushAsync(ct);
            fileStream.Close();

            // Atomic rename: delete target if exists, then move tmp into place
            if (File.Exists(targetPath))
                File.Delete(targetPath);

            File.Move(tmpPath, targetPath);

            Log.Information("UpdateDownloader: Complete, {Bytes:N0} bytes written", totalRead);
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
