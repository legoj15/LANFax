namespace LANFax.Tests;

public class InstallScriptTests
{
    private static string Dir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "install"))) d = d.Parent;
        return Path.Combine(d!.FullName, "install");
    }

    [Fact]
    public void InstallScriptHasRequiredCmdlets()
    {
        var t = File.ReadAllText(Path.Combine(Dir(), "Install-LANFaxPrinter.ps1"));
        Assert.Contains("Add-PrinterPort", t);
        Assert.Contains("Add-Printer ", t);
        Assert.Contains("Microsoft Print To PDF", t);
        Assert.Contains("RunAs", t);
        Assert.Contains("print.pdf", t);
        Assert.Contains("BUILTIN\\Users", t);
        Assert.DoesNotContain(@"C:\Program Files (x86)", t);
    }

    [Fact]
    public void InstallScriptGrantsUsersModifyOnTheDataDirNotOnlySpool()
    {
        // config.json lives in %ProgramData%\LANFax, so every Windows user must be able to write there,
        // inherited to spool\ and to files created later.
        var t = File.ReadAllText(Path.Combine(Dir(), "Install-LANFaxPrinter.ps1"));
        Assert.Matches(@"\[string\]\$DataDir\s*=\s*\(Join-Path \$env:ProgramData 'LANFax'\)", t);
        Assert.Matches(@"Grant-UsersModify\s+-Path\s+\$DataDir", t);
        Assert.Contains("'ContainerInherit,ObjectInherit'", t);
        Assert.Contains("-DataDir `\"$DataDir`\"", t); // forwarded through the self-elevation relaunch
        Assert.DoesNotMatch(@"Set-Acl\s+-Path\s+\$SpoolDir", t);
    }

    [Theory]
    [InlineData("Install-LANFaxPrinter.ps1")]
    [InlineData("Uninstall-LANFaxPrinter.ps1")]
    public void SelfElevationPassesOneCommandLineString(string script)
    {
        // Windows PowerShell 5.1 re-quotes pre-quoted -ArgumentList array entries that contain spaces, splitting
        // "C:\Program Files\LANFax\..." into several arguments. One command-line string behaves the same on 5.1 and 7.
        var t = File.ReadAllText(Path.Combine(Dir(), script));
        Assert.DoesNotContain("'\"{0}\"' -f", t);
        Assert.Matches(@"(?m)^\s*\$argLine = ""-NoProfile -ExecutionPolicy Bypass -File `""\$PSCommandPath`"".*""\s*$", t);
        Assert.Contains("-ArgumentList $argLine", t);
        Assert.Contains("-PrinterName `\"$PrinterName`\"", t);
        Assert.Contains("-SpoolDir `\"$SpoolDir`\"", t);
    }

    [Theory]
    [InlineData("Install-LANFaxPrinter.ps1", true)]
    [InlineData("Uninstall-LANFaxPrinter.ps1", false)]
    public async Task SelfElevationArgumentsRoundTripPathsWithSpacesOnWindowsPowerShell51(string script, bool hasDataDir)
    {
        var ps51 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(ps51)) return; // not Windows PowerShell 5.1: the static check above still runs
        var line = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(Path.Combine(Dir(), script)),
            @"(?m)^\s*(\$argLine = .*)$").Groups[1].Value.TrimEnd();
        Assert.NotEmpty(line);

        // A driver in a folder with a space runs the script's own $argLine through Start-Process (as the relaunch
        // does, minus -Verb RunAs) into a child of the same powershell.exe, which writes back the parameters it got.
        var tmp = Path.Combine(Path.GetTempPath(), "lanfax ps " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var outFile = Path.Combine(tmp, "out.txt");
            var driver = Path.Combine(tmp, "driver script.ps1");
            File.WriteAllText(driver, string.Join("\r\n",
                "param([string]$DataDir, [string]$SpoolDir, [string]$PrinterName)",
                "if ($env:LANFAX_PROBE_CHILD) { Set-Content -LiteralPath $env:LANFAX_PROBE_OUT -Encoding UTF8 -Value @($DataDir, $SpoolDir, $PrinterName); exit 0 }",
                "$env:LANFAX_PROBE_CHILD = '1'",
                line,
                "$p = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList $argLine -Wait -PassThru -NoNewWindow",
                "exit $p.ExitCode",
                ""));
            string dataDir = @"C:\Program Files\LAN Fax Data", spoolDir = @"C:\Program Files\LAN Fax Data\spool dir";
            var psi = new System.Diagnostics.ProcessStartInfo(ps51)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", driver, "-SpoolDir", spoolDir, "-PrinterName", "LAN Fax" })
                psi.ArgumentList.Add(a);
            if (hasDataDir) { psi.ArgumentList.Add("-DataDir"); psi.ArgumentList.Add(dataDir); }
            psi.Environment["LANFAX_PROBE_OUT"] = outFile;
            psi.Environment.Remove("LANFAX_PROBE_CHILD");
            using var p = System.Diagnostics.Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            await p.WaitForExitAsync(cts.Token);
            Assert.True(p.ExitCode == 0 && File.Exists(outFile), $"exit {p.ExitCode}: {await stderr} {await stdout}");
            var got = File.ReadAllLines(outFile);
            Assert.Equal(new[] { hasDataDir ? dataDir : "", spoolDir, "LAN Fax" }, got);
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    [Fact]
    public void UninstallScriptSaysTheDataFolderIsKept()
    {
        // The installer's [Dirs] use uninsneveruninstall: config.json, unsent documents and kept copies survive.
        var t = File.ReadAllText(Path.Combine(Dir(), "Uninstall-LANFaxPrinter.ps1"));
        var synopsis = t[..t.IndexOf("#>", StringComparison.Ordinal)];
        Assert.Contains(@"Leaves %ProgramData%\LANFax", synopsis);
        Assert.Contains("config.json", synopsis);
    }

    [Fact]
    public void UninstallScriptRemovesPrinterThenPort()
    {
        var t = File.ReadAllText(Path.Combine(Dir(), "Uninstall-LANFaxPrinter.ps1"));
        var p = t.IndexOf("Remove-Printer ", StringComparison.Ordinal);
        var q = t.IndexOf("Remove-PrinterPort", StringComparison.Ordinal);
        Assert.True(p >= 0 && q > p);
        Assert.Contains("RunAs", t);
        Assert.DoesNotContain(@"C:\Program Files (x86)", t);
    }

    [Fact]
    public void InstallScriptRebindsAnExistingPrinterOnAnotherPort()
    {
        // A "LAN Fax" queue left on another port would print to a path nobody watches.
        var t = File.ReadAllText(Path.Combine(Dir(), "Install-LANFaxPrinter.ps1"));
        Assert.Matches(@"\.PortName\s+-ne\s+\$portName", t);
        Assert.Matches(@"Set-Printer\s+-Name\s+\$PrinterName\s+-PortName\s+\$portName", t);
        Assert.True(t.IndexOf("Set-Printer", StringComparison.Ordinal) > t.IndexOf("Add-PrinterPort", StringComparison.Ordinal),
            "the port must exist before the printer is moved onto it");
    }
}
