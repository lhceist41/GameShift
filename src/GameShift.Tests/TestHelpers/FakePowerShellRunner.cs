using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using GameShift.Core.System;

namespace GameShift.Tests.TestHelpers;

/// <summary>
/// Stand-in for <see cref="ParameterizedPowerShell.Run"/>: records every start request (file name,
/// argument line, decoded script body after the module import, the imported module manifest and the
/// GS_ARG values from the child's environment) and returns a scripted result, so no PowerShell,
/// Defender or firewall call ever runs. A request whose script does not start with the module import
/// or whose PSModulePath is not the System32 modules folder alone is rejected.
/// </summary>
public sealed class FakePowerShellRunner
{
    public sealed record Call(string FileName, string Arguments, string Script, string ModuleManifest, IReadOnlyList<string> Values);

    private const string EncodedPrefix = "-NoProfile -NonInteractive -EncodedCommand ";
    private const string ImportStatement = "Import-Module -Name $env:GS_MODULE -ErrorAction Stop\n";

    public static string SystemModulesDirectory =>
        Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\Modules");

    public static string ManifestPath(string module) =>
        Path.Combine(SystemModulesDirectory, module, module + ".psd1");

    public List<Call> Calls { get; } = new();

    /// <summary>Decides the result for each call; default is a clean exit 0 with no output.</summary>
    public Func<Call, ProcessRunResult> Respond { get; set; } =
        _ => new ProcessRunResult(0, string.Empty, string.Empty, TimedOut: false, Exited: true);

    public ProcessRunResult Run(ProcessStartInfo psi, int timeoutMs)
    {
        if (!psi.Arguments.StartsWith(EncodedPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected PowerShell argument line: " + psi.Arguments);

        var fullScript = Encoding.Unicode.GetString(Convert.FromBase64String(psi.Arguments[EncodedPrefix.Length..]));
        if (!fullScript.StartsWith(ImportStatement, StringComparison.Ordinal))
            throw new InvalidOperationException("Script does not start with the module import: " + fullScript);
        if (!psi.Environment.TryGetValue("PSModulePath", out var modulePath) ||
            !string.Equals(modulePath, SystemModulesDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected child PSModulePath: " + modulePath);
        var script = fullScript[ImportStatement.Length..];
        var manifest = psi.Environment.TryGetValue("GS_MODULE", out var m) ? m ?? string.Empty : string.Empty;
        var values = new List<string>();
        for (int i = 0; psi.Environment.TryGetValue(ParameterizedPowerShell.ArgumentVariablePrefix + i, out var v) && v != null; i++)
            values.Add(v);

        var call = new Call(psi.FileName, psi.Arguments, script, manifest, values);
        Calls.Add(call);
        return Respond(call);
    }

    public IEnumerable<Call> CallsFor(string script) => Calls.Where(c => c.Script == script);

    public static string ExpectedArguments(string script) =>
        EncodedPrefix + Convert.ToBase64String(Encoding.Unicode.GetBytes(ImportStatement + script));

    public static ProcessRunResult Exit(int code, string stdout = "") =>
        new(code, stdout, string.Empty, TimedOut: false, Exited: true);
}
