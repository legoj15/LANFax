using System.ComponentModel;
using System.Diagnostics;
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

/// <param name="run">Starts the process and completes when it exits (tests fake it). Defaults to an elevated Process.Start.</param>
public sealed class PrinterSetup(IPrinterKeyReader reader, Func<ProcessStartInfo, Task>? run = null)
{
    public PrinterSetup() : this(new RegistryPrinterKeyReader()) { }

    private readonly Func<ProcessStartInfo, Task> _run = run ?? RunProcessAsync;

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
    /// One command-line string for the elevated powershell.exe. A quote inside a value would end its argument early,
    /// so it is refused (<see cref="ArgumentException"/>; <c>ClientConfig.Validate</c> refuses it too). A trailing
    /// backslash would escape the closing quote (<c>"C:\dir\"</c>): the spool dir's is trimmed, and any that remain
    /// (a root such as <c>D:\</c>) are doubled, which argv parsing turns back into one.
    /// </summary>
    public static string BuildArguments(string script, string spoolDir, string printerName) =>
        $"-NoProfile -ExecutionPolicy Bypass -File {Quote(script)} -SpoolDir {Quote(TrimSlashes(spoolDir))} -PrinterName {Quote(printerName)}";

    private static string Quote(string value)
    {
        if (value.Contains('"')) throw new ArgumentException($"\"{value}\" contains a quote (\"), which cannot be passed to the install script.");
        int trailing = value.Length - value.TrimEnd('\\').Length;
        return "\"" + value + new string('\\', trailing) + "\"";
    }

    private static string TrimSlashes(string p)
    {
        var t = p.TrimEnd('\\', '/');
        return t.Length == 0 || t.EndsWith(':') ? p : t; // leave "C:\" alone rather than make it "C:"
    }

    /// <summary>
    /// Runs the install script elevated (UAC prompt) without blocking the UI thread, then re-checks that the printer
    /// exists on the right port (the script moves an existing one). Null on success, else a message.
    /// </summary>
    public async Task<string?> InstallAsync(string spoolDir, string printerName, string? script = null)
    {
        script ??= ScriptPath;
        if (!File.Exists(script)) return "Install script not found: " + script;
        string args;
        try { args = BuildArguments(script, spoolDir, printerName); }
        catch (ArgumentException e) { return "Cannot install the printer: " + e.Message; }
        try
        {
            await _run(new ProcessStartInfo("powershell.exe", args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return "Installation cancelled."; }
        catch (Exception e) { return "Could not start the installer: " + e.Message; }
        var st = Status(printerName);
        return st.State switch
        {
            PrinterState.Installed => null,
            PrinterState.Misconfigured => $"The printer still points at {st.ActualPort}, not {ExpectedPort()} (run the install script manually).",
            _ => "The printer was not installed (see the script output or run it manually).",
        };
    }

    private static async Task RunProcessAsync(ProcessStartInfo psi)
    {
        using var p = Process.Start(psi);
        if (p != null) await p.WaitForExitAsync();
    }
}
