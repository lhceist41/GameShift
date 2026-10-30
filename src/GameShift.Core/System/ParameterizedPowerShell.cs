using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace GameShift.Core.System;

/// <summary>
/// Runs a constant Windows PowerShell script with its data passed through the child's environment
/// (<c>$env:GS_ARG0</c>, <c>$env:GS_ARG1</c>, ...), never through the script text. A value can then
/// contain any quote character, <c>;</c>, <c>$(</c>, backtick or newline without changing what the
/// elevated script does, because PowerShell never parses it as code. The child resolves modules
/// only from System32 and imports the script's module by absolute path.
/// </summary>
public static class ParameterizedPowerShell
{
    /// <summary>Prefix of the environment variables that carry the script's values.</summary>
    public const string ArgumentVariablePrefix = "GS_ARG";

    /// <summary>Environment variable that carries the absolute manifest path of the imported module.</summary>
    public const string ModuleVariable = "GS_MODULE";

    /// <summary>Prepended to every script so its cmdlets come from the System32 module, not an autoloaded one.</summary>
    public const string ImportModuleStatement = "Import-Module -Name $env:GS_MODULE -ErrorAction Stop\n";

    /// <summary>
    /// The Windows PowerShell modules folder under System32, from the Win32 system directory and
    /// never from an environment variable the user controls.
    /// </summary>
    public static string SystemModulesDirectory =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "Modules");

    /// <summary>
    /// Builds the start info for <paramref name="script"/>: absolute powershell.exe path,
    /// <c>-NoProfile -NonInteractive -EncodedCommand</c>, <c>PSModulePath</c> limited to
    /// <see cref="SystemModulesDirectory"/>, <paramref name="module"/> imported from there by its
    /// absolute manifest path, and value <c>i</c> in <c>GS_ARG{i}</c> of the child's environment.
    /// </summary>
    /// <param name="module">System32 module the script's cmdlets come from, e.g. <c>ConfigDefender</c>.</param>
    /// <param name="script">The script text. Must be a constant; reference values as <c>$env:GS_ARG{i}</c>.</param>
    /// <param name="values">Values for <c>GS_ARG0</c>, <c>GS_ARG1</c>, ... in order.</param>
    public static ProcessStartInfo CreateStartInfo(
        [ConstantExpected] string module, [ConstantExpected] string script, params string[] values)
    {
        if (string.IsNullOrEmpty(module) || !module.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("Module must be a plain System32 module name.", nameof(module));

        var psi = new ProcessStartInfo
        {
            FileName = NativeInterop.SystemExePath("WindowsPowerShell\\v1.0\\powershell.exe"),
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " + EncodeCommand(ImportModuleStatement + script),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // Only the values passed here reach the script, never an inherited GS_ARG variable.
        foreach (var key in psi.Environment.Keys.ToList())
        {
            if (key.StartsWith(ArgumentVariablePrefix, StringComparison.OrdinalIgnoreCase))
                psi.Environment.Remove(key);
        }

        for (int i = 0; i < values.Length; i++)
        {
            var value = values[i] ?? throw new ArgumentNullException(nameof(values));
            // An environment block cannot carry NUL; the child would see a truncated value.
            if (value.Contains('\0'))
                throw new ArgumentException("A PowerShell argument value must not contain NUL.", nameof(values));
            psi.Environment[ArgumentVariablePrefix + i] = value;
        }

        // Modules come only from the admin-only System32 folder.
        var modulesDirectory = SystemModulesDirectory;
        psi.Environment["PSModulePath"] = modulesDirectory;
        psi.Environment[ModuleVariable] = Path.Combine(modulesDirectory, module, module + ".psd1");

        return psi;
    }

    /// <summary>The <c>-EncodedCommand</c> form of <paramref name="script"/> (Base64 of UTF-16LE).</summary>
    public static string EncodeCommand(string script) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    /// <summary>
    /// Starts <paramref name="psi"/>, drains stdout and stderr on worker threads, waits up to
    /// <paramref name="timeoutMs"/> and kills the process tree on timeout. Same result contract as
    /// <see cref="ProcessRunner.Run"/>, which cannot carry an environment. Stdout is decoded as
    /// UTF-8; a script whose output is read sets <c>[Console]::OutputEncoding</c> to UTF-8.
    /// </summary>
    public static ProcessRunResult Run(ProcessStartInfo psi, int timeoutMs)
    {
        if (psi.RedirectStandardOutput)
            psi.StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        using var process = Process.Start(psi);
        if (process == null)
            return new ProcessRunResult(0, string.Empty, string.Empty, TimedOut: false, Exited: false);

        string stdout = string.Empty, stderr = string.Empty;
        var outTask = Task.Run(() => { try { stdout = process.StandardOutput.ReadToEnd(); } catch { /* best effort */ } });
        var errTask = Task.Run(() => { try { stderr = process.StandardError.ReadToEnd(); } catch { /* best effort */ } });

        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            Task.WaitAll(new[] { outTask, errTask }, 2000);
            return new ProcessRunResult(0, stdout, stderr, TimedOut: true, Exited: false);
        }

        Task.WaitAll(new[] { outTask, errTask }, 2000);
        return new ProcessRunResult(process.ExitCode, stdout, stderr, TimedOut: false, Exited: true);
    }
}
