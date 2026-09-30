using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using LANFax.Core;
using Microsoft.Win32;

namespace LANFax.App.Logic;

public interface IPrinterKeyReader
{
    bool Exists(string printerName);
    /// <summary>The printer's port name (its <c>Port</c> value), "" when the value is missing, null when there is no such printer.</summary>
    string? Port(string printerName);
}

public enum PrinterState
{
    /// <summary>The printer exists and prints to the LANFax pipe.</summary>
    Installed,
    /// <summary>
    /// The printer exists but prints somewhere else (<see cref="PrinterStatus.ActualPort"/>): jobs would vanish. This
    /// includes the old file port <c>spool\print.pdf</c> (installs before 2026-09-30), which the install script moves.
    /// </summary>
    Misconfigured,
    Missing,
}

public sealed record PrinterStatus(PrinterState State, string? ActualPort);

/// <summary>
/// The tray's periodic printer check: remembers the last status seen and says which new one to announce. A change
/// (state or port) to Missing or Misconfigured is announced once; the same status on later ticks, and a change back to
/// Installed, are not. The start-up status is the initial one (the start-up balloon covers it).
/// </summary>
public sealed class PrinterWatch(PrinterStatus initial)
{
    public PrinterStatus Last { get; private set; } = initial;

    /// <summary>The status to announce, or null when there is nothing new to warn about.</summary>
    public PrinterStatus? Observe(PrinterStatus now)
    {
        bool changed = now != Last;
        Last = now;
        return changed && now.State != PrinterState.Installed ? now : null;
    }
}

/// <summary>
/// Reads HKLM\SYSTEM\CurrentControlSet\Control\Print\Printers\&lt;name&gt; and its <c>Port</c> value (no process
/// spawn).
/// </summary>
public sealed class RegistryPrinterKeyReader : IPrinterKeyReader
{
    private const string Root = @"SYSTEM\CurrentControlSet\Control\Print\Printers\";

    /// <summary>The key for a local printer, or null for a name that cannot be one (empty, or with a backslash).</summary>
    public static string? KeyPath(string printerName) =>
        string.IsNullOrEmpty(printerName) || printerName.Contains('\\') ? null : Root + printerName;

    public bool Exists(string printerName)
    {
        if (KeyPath(printerName) is not { } key) return false;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(key);
            return k != null;
        }
        catch (Exception) { return false; }
    }

    public string? Port(string printerName)
    {
        if (KeyPath(printerName) is not { } key) return null;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(key);
            return k == null ? null : k.GetValue("Port") as string ?? "";
        }
        catch (Exception) { return null; }
    }
}

/// <summary>
/// What the tray's printer install did: <paramref name="Error"/> is null on success, else the message to show.
/// <paramref name="Output"/> is every line the install script wrote; <paramref name="Warnings"/> those starting with
/// "warning:" (a migration step that failed without stopping the install, such as moving a leftover print.pdf).
/// </summary>
public sealed record InstallResult(string? Error, IReadOnlyList<string> Warnings, IReadOnlyList<string> Output)
{
    public static InstallResult Failed(string error, IReadOnlyList<string>? output = null) => new(error, [], output ?? []);
}

/// <param name="run">
/// Starts the process and returns its exit code once it exits (tests fake it); the second argument is the file the
/// command writes the script's output to. Defaults to an elevated Process.Start.
/// </param>
public sealed class PrinterSetup(IPrinterKeyReader reader, Func<ProcessStartInfo, string, Task<int>>? run = null)
{
    public PrinterSetup() : this(new RegistryPrinterKeyReader()) { }

    private readonly Func<ProcessStartInfo, string, Task<int>> _run = run ?? RunProcessAsync;

    /// <summary>The port a correctly installed printer uses: the listener's pipe, <c>\\.\pipe\LANFax</c>, as the install script builds it.</summary>
    public static string ExpectedPort() => PrintPipeListener.PortName(PrintPipeListener.DefaultPipeName);

    /// <summary>Whether the printer exists and prints to the pipe the app listens on (compared case-insensitively).</summary>
    public PrinterStatus Status(string printerName)
    {
        var port = reader.Port(printerName);
        if (port == null) return new PrinterStatus(PrinterState.Missing, null);
        return new PrinterStatus(string.Equals(port, ExpectedPort(), StringComparison.OrdinalIgnoreCase)
            ? PrinterState.Installed : PrinterState.Misconfigured, port);
    }

    /// <summary>The script the csproj copies to install\ beside the exe (and the installer lays down there too).</summary>
    public static string ScriptPath => Path.Combine(AppContext.BaseDirectory, "install", "Install-LANFaxPrinter.ps1");

    /// <summary>
    /// The PowerShell the elevated powershell.exe runs: the install script with every line it writes (all streams)
    /// appended to <paramref name="outPath"/> as it goes, and "error: ..." plus exit code 1 when it throws. The exit code
    /// is 0 unless the script throws or ends with its own <c>exit N</c> (N kept). An elevated
    /// process started through UAC (ShellExecute) cannot have its output redirected, so it goes through a file.
    /// Values are PowerShell single-quoted literals. A double quote in a value is refused
    /// (<see cref="ArgumentException"/>): the script refuses it too, and <c>ClientConfig.Validate</c> does.
    /// </summary>
    public static string BuildCommand(string script, string spoolDir, string printerName, string outPath) =>
        $"$out = {Literal(outPath)}\n" +
        "try {\n" +
        $"  & {Literal(script)} -SpoolDir {Literal(TrimSlashes(spoolDir))} -PrinterName {Literal(printerName)} *>&1 | ForEach-Object {{ Add-Content -LiteralPath $out -Encoding UTF8 -Value \"$_\" }}\n" +
        // $? is false only when the script ended with its own non-zero exit; a native command it ran and tolerated
        // leaves $LASTEXITCODE non-zero with $? true (measured on 5.1 and 7), and is not a failure.
        "  if (-not $?) { exit $LASTEXITCODE }\n" +
        "  exit 0\n" +
        "}\n" +
        "catch {\n" +
        "  Add-Content -LiteralPath $out -Encoding UTF8 -Value ('error: ' + $_.Exception.Message)\n" +
        "  exit 1\n" +
        "}\n";

    /// <summary>
    /// The elevated powershell.exe's command line: <see cref="BuildCommand"/> as -EncodedCommand (UTF-16LE, base64),
    /// so no value needs command-line quoting.
    /// </summary>
    public static string BuildArguments(string script, string spoolDir, string printerName, string outPath) =>
        "-NoProfile -ExecutionPolicy Bypass -NonInteractive -EncodedCommand " +
        Convert.ToBase64String(Encoding.Unicode.GetBytes(BuildCommand(script, spoolDir, printerName, outPath)));

    /// <summary>A PowerShell single-quoted literal: every single-quote character PowerShell accepts is doubled.</summary>
    private static string Literal(string value)
    {
        if (value.Contains('"')) throw new ArgumentException($"\"{value}\" contains a quote (\"), which cannot be passed to the install script.");
        return "'" + Regex.Replace(value, "['\u2018\u2019\u201A\u201B]", "$0$0") + "'";
    }

    private static string TrimSlashes(string p)
    {
        var t = p.TrimEnd('\\', '/');
        return t.Length == 0 || t.EndsWith(':') ? p : t; // leave "C:\" alone rather than make it "C:"
    }

    /// <summary>
    /// Runs the install script elevated (UAC prompt) without blocking the UI thread, reads what it wrote, then
    /// re-checks that the printer exists on the right port (the script moves an existing one). A non-zero exit code
    /// is a failure even when the port reads right. Warnings are returned for the caller to show and log.
    /// </summary>
    public async Task<InstallResult> InstallAsync(string spoolDir, string printerName, string? script = null)
    {
        script ??= ScriptPath;
        if (!File.Exists(script)) return InstallResult.Failed("Install script not found: " + script);
        var outPath = Path.Combine(Path.GetTempPath(), $"lanfax-install-{Guid.NewGuid():N}.log");
        string args;
        try { args = BuildArguments(script, spoolDir, printerName, outPath); }
        catch (ArgumentException e) { return InstallResult.Failed("Cannot install the printer: " + e.Message); }
        int code;
        try
        {
            code = await _run(new ProcessStartInfo("powershell.exe", args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            }, outPath);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return InstallResult.Failed("Installation cancelled."); }
        catch (Exception e) { return InstallResult.Failed("Could not start the installer: " + e.Message); }
        IReadOnlyList<string> output = ReadOutput(outPath);
        var warnings = output.Where(l => l.StartsWith("warning:", StringComparison.OrdinalIgnoreCase)).ToList();
        if (code != 0)
        {
            var last = output.LastOrDefault(l => l.StartsWith("error:", StringComparison.OrdinalIgnoreCase));
            return InstallResult.Failed($"The printer install script failed (exit code {code}){(last != null ? ": " + last : ".")}", output);
        }
        var st = Status(printerName);
        return st.State switch
        {
            PrinterState.Installed => new InstallResult(null, warnings, output),
            PrinterState.Misconfigured => InstallResult.Failed($"The printer still points at {st.ActualPort}, not {ExpectedPort()} (run the install script manually).", output),
            _ => InstallResult.Failed("The printer was not installed (see the log or run the install script manually).", output),
        };
    }

    /// <summary>The script's lines (none when it wrote nothing), then the file is removed.</summary>
    private static string[] ReadOutput(string path)
    {
        try { return File.Exists(path) ? File.ReadAllLines(path).Where(l => l.Length > 0).ToArray() : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ["warning: the install script output could not be read: " + e.Message]; }
        finally { try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    private static async Task<int> RunProcessAsync(ProcessStartInfo psi, string outPath)
    {
        using var p = Process.Start(psi);
        if (p == null) return 0;
        await p.WaitForExitAsync();
        return p.ExitCode;
    }
}
