using System;
using System.IO;

namespace GameShift.Tests.TestHelpers;

/// <summary>
/// Test helper that creates a unique temp directory and deletes it on dispose.
/// Use via: using var temp = new TempPath();
/// </summary>
public sealed class TempPath : IDisposable
{
    public string Path { get; }

    public TempPath()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "gameshift-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string GetFile(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch { /* Best-effort cleanup */ }
    }
}
