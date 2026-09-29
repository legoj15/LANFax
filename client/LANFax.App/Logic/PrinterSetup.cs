using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace LANFax.App.Logic;

public interface IPrinterKeyReader
{
    bool Exists(string printerName);
}

/// <summary>Looks for HKLM\SYSTEM\CurrentControlSet\Control\Print\Printers\&lt;name&gt; (no process spawn).</summary>
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
}

/// <param name="run">Starts the process and completes when it exits (tests fake it). Defaults to an elevated Process.Start.</param>
public sealed class PrinterSetup(IPrinterKeyReader reader, Func<ProcessStartInfo, Task>? run = null)
{
    public PrinterSetup() : this(new RegistryPrinterKeyReader()) { }

    private readonly Func<ProcessStartInfo, Task> _run = run ?? RunProcessAsync;

    public bool IsInstalled(string printerName) => reader.Exists(printerName);

    /// <summary>The script the csproj copies to install\ beside the exe (and the installer lays down there too).</summary>
    public static string ScriptPath => Path.Combine(AppContext.BaseDirectory, "install", "Install-LANFaxPrinter.ps1");

    /// <summary>A trailing backslash would escape the closing quote (<c>"C:\dir\"</c>), so it is trimmed.</summary>
    public static string BuildArguments(string script, string spoolDir, string printerName) =>
        $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -SpoolDir \"{TrimSlashes(spoolDir)}\" -PrinterName \"{printerName}\"";

    private static string TrimSlashes(string p)
    {
        var t = p.TrimEnd('\\', '/');
        return t.Length == 0 || t.EndsWith(':') ? p : t; // leave "C:\" alone rather than make it "C:"
    }

    /// <summary>Runs the install script elevated (UAC prompt) without blocking the UI thread, then re-checks. Null on success, else a message.</summary>
    public async Task<string?> InstallAsync(string spoolDir, string printerName, string? script = null)
    {
        script ??= ScriptPath;
        if (!File.Exists(script)) return "Install script not found: " + script;
        try
        {
            await _run(new ProcessStartInfo("powershell.exe", BuildArguments(script, spoolDir, printerName))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return "Installation cancelled."; }
        catch (Exception e) { return "Could not start the installer: " + e.Message; }
        return IsInstalled(printerName) ? null : "The printer was not installed (see the script output or run it manually).";
    }

    private static async Task RunProcessAsync(ProcessStartInfo psi)
    {
        using var p = Process.Start(psi);
        if (p != null) await p.WaitForExitAsync();
    }
}
