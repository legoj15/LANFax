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
    public bool Exists(string printerName)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Print\Printers\" + printerName);
            return k != null;
        }
        catch (Exception) { return false; }
    }
}

public sealed class PrinterSetup(IPrinterKeyReader reader)
{
    public PrinterSetup() : this(new RegistryPrinterKeyReader()) { }

    public bool IsInstalled(string printerName) => reader.Exists(printerName);

    public static string ScriptPath =>
        Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "install", "Install-LANFaxPrinter.ps1");

    public static string BuildArguments(string script, string spoolDir, string printerName) =>
        $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -SpoolDir \"{spoolDir}\" -PrinterName \"{printerName}\"";

    /// <summary>Runs the install script elevated (UAC prompt), waits, then re-checks. Returns null on success, else a message.</summary>
    public string? Install(string spoolDir, string printerName)
    {
        var script = ScriptPath;
        if (!File.Exists(script)) return "Install script not found: " + script;
        try
        {
            var psi = new ProcessStartInfo("powershell.exe", BuildArguments(script, spoolDir, printerName))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit();
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return "Installation cancelled."; }
        catch (Exception e) { return "Could not start the installer: " + e.Message; }
        return IsInstalled(printerName) ? null : "The printer was not installed (see the script output or run it manually).";
    }
}
