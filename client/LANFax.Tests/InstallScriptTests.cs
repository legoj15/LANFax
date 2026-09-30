using System.Security.AccessControl;
using System.Security.Principal;

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
        Assert.Contains("-DataDir $(ConvertTo-QuotedArg $DataDir)", t); // forwarded through the self-elevation relaunch
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
        Assert.Matches(@"(?m)^\s*\$argLine = ""-NoProfile -ExecutionPolicy Bypass -File \$\(ConvertTo-QuotedArg \$PSCommandPath\).*""\s*$", t);
        Assert.Contains("-ArgumentList $argLine", t);
        Assert.Contains("-PrinterName $(ConvertTo-QuotedArg $PrinterName)", t);
        Assert.Contains("-SpoolDir $(ConvertTo-QuotedArg $SpoolDir)", t);
    }

    [Theory]
    [InlineData("Install-LANFaxPrinter.ps1")]
    [InlineData("Uninstall-LANFaxPrinter.ps1")]
    public void SelfElevationRefusesQuotesAndProtectsTheClosingQuote(string script)
    {
        // A quote inside a value would end its argument early in the one-string relaunch; a trailing backslash would
        // escape the closing quote. Values are checked before anything runs, and quoted by one helper.
        var t = File.ReadAllText(Path.Combine(Dir(), script));
        Assert.Matches(@"(?ms)^function ConvertTo-QuotedArg\(\[string\]\$Value\) \{.*?\$Value\.Contains\('""'\).*?throw.*?-replace '\(\\\\\+\)\$', '\$1\$1'.*?^\}", t);
        var check = t.IndexOf("foreach ($value in", StringComparison.Ordinal);
        Assert.True(check > 0 && check < t.IndexOf("$isAdmin", StringComparison.Ordinal), "values must be checked before the relaunch");
    }

    [Theory]
    [InlineData("Install-LANFaxPrinter.ps1", true)]
    [InlineData("Uninstall-LANFaxPrinter.ps1", false)]
    public async Task SelfElevationArgumentsRoundTripPathsWithSpacesOnWindowsPowerShell51(string script, bool hasDataDir)
    {
        var ps51 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(ps51)) return; // not Windows PowerShell 5.1: the static check above still runs
        var text = File.ReadAllText(Path.Combine(Dir(), script));
        var line = System.Text.RegularExpressions.Regex.Match(text, @"(?m)^\s*(\$argLine = .*)$").Groups[1].Value.TrimEnd();
        var quoter = System.Text.RegularExpressions.Regex.Match(text, @"(?ms)^function ConvertTo-QuotedArg.*?^\}").Value;
        Assert.NotEmpty(line);
        Assert.NotEmpty(quoter);

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
                quoter,
                line,
                "$p = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList $argLine -Wait -PassThru -NoNewWindow",
                "exit $p.ExitCode",
                ""));
            // The spool dir ends in a backslash: it must survive without escaping the closing quote.
            string dataDir = @"C:\Program Files\LAN Fax Data", spoolDir = @"C:\Program Files\LAN Fax Data\spool dir\";
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

    // ---- pause and resume: the queue is paused whenever no watcher claims print.pdf ----

    [Fact]
    public void InstallScriptGrantsUsersPauseAndResumeOnThisPrinterOnly()
    {
        // A standard user's tray app pauses the queue on exit and resumes it on start. Only the installing user gets
        // "Manage printers" by default, so Users get PRINTER_ACCESS_ADMINISTER on this one printer (not server-wide,
        // not delete, not change permissions).
        var t = File.ReadAllText(Path.Combine(Dir(), "Install-LANFaxPrinter.ps1"));
        Assert.Matches(@"Get-Printer\s+-Name\s+\$PrinterName\s+-Full", t);
        Assert.Matches(@"Set-Printer\s+-Name\s+\$PrinterName\s+-PermissionSDDL", t);
        Assert.Contains("S-1-5-32-545", t); // BUILTIN\Users by SID: not localized
        Assert.True(t.IndexOf("-PermissionSDDL", StringComparison.Ordinal) > t.IndexOf("Add-Printer ", StringComparison.Ordinal),
            "the printer must exist before its permissions are set");
    }

    [Fact]
    public void InstallScriptKeepsPrintedJobsSoAReplacedDocumentCanBePrintedAgain()
    {
        // With KEEPPRINTEDJOBS a document whose print.pdf was replaced (app killed or crashed, queue left printing)
        // is still in the queue as "Printed", and the app restarts it at its next start.
        var t = File.ReadAllText(Path.Combine(Dir(), "Install-LANFaxPrinter.ps1"));
        Assert.Matches(@"\.KeepPrintedJobs\s*=\s*\$true", t);
        Assert.Contains("Set-CimInstance", t);
        var keep = t.IndexOf(".KeepPrintedJobs", StringComparison.Ordinal);
        Assert.True(keep > t.IndexOf("Add-Printer ", StringComparison.Ordinal), "the printer must exist first");
        Assert.True(keep < t.IndexOf("-MethodName Pause", StringComparison.Ordinal), "before the queue is paused");
    }

    [Fact]
    public void UninstallScriptCountsOnlyDocumentsStillWaiting()
    {
        // Kept printed jobs are already sent or claimed; only unprinted ones are really discarded.
        var t = File.ReadAllText(Path.Combine(Dir(), "Uninstall-LANFaxPrinter.ps1"));
        Assert.Matches(@"JobStatus\S{0,3}\s+-notmatch\s+'Printed'", t);
    }

    [Fact]
    public void InstallScriptLeavesTheQueuePausedForTheAppToResume()
    {
        var t = File.ReadAllText(Path.Combine(Dir(), "Install-LANFaxPrinter.ps1"));
        var pause = t.IndexOf("-MethodName Pause", StringComparison.Ordinal);
        Assert.True(pause > 0, "no pause");
        Assert.True(pause > t.IndexOf("-PermissionSDDL", StringComparison.Ordinal) && pause > t.IndexOf("Set-Printer -Name $PrinterName -PortName", StringComparison.Ordinal),
            "pause once the queue is in its final state");
        Assert.Contains("Win32_Printer", t);
    }

    [Fact]
    public void InstallScriptDoesNotPauseTheQueueUnderARunningLANFax()
    {
        // A running tray app (a repair, an upgrade the user kept it open for, another user's session) already watches
        // the port; pausing under it would hold every later document with no word to anyone.
        var t = File.ReadAllText(Path.Combine(Dir(), "Install-LANFaxPrinter.ps1"));
        var running = t.IndexOf("Get-Process -Name LANFax", StringComparison.Ordinal);
        var pause = t.IndexOf("-MethodName Pause", StringComparison.Ordinal);
        Assert.True(running > 0 && running < pause, "check for a running LANFax before pausing");
        Assert.Matches(@"(?s)if\s*\(\s*\$lanfaxRunning\s*\).*?else\s*\{[^}]*-MethodName Pause", t);
    }

    [Fact]
    public void InstallScriptIsHonestAboutTheRightsItGrantsAndPrintsACleanStatus()
    {
        var t = File.ReadAllText(Path.Combine(Dir(), "Install-LANFaxPrinter.ps1"));
        Assert.DoesNotContain("and nothing else", t); // PRINTER_ACCESS_ADMINISTER also covers the port, driver and settings
        var last = t.TrimEnd().Split('\n')[^1].TrimEnd();
        Assert.False(last.EndsWith("\\\"", StringComparison.Ordinal), "a backslash does not escape a quote in PowerShell: " + last);
    }

    [Fact]
    public void UninstallScriptNeverLeavesAPausedQueueBehind()
    {
        // Removing the printer removes its paused state; if the removal fails the queue is resumed, not left paused.
        var t = File.ReadAllText(Path.Combine(Dir(), "Uninstall-LANFaxPrinter.ps1"));
        Assert.Matches(@"(?s)try\s*\{\s*Remove-Printer -Name \$PrinterName.*?\}\s*catch\s*\{.*?-MethodName Resume.*?throw", t);
    }

    [Fact]
    public async Task PauseAceIsAddedOnceAndKeepsEveryExistingAce()
    {
        var ps51 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(ps51)) return;
        var text = File.ReadAllText(Path.Combine(Dir(), "Install-LANFaxPrinter.ps1"));
        var fn = System.Text.RegularExpressions.Regex.Match(text, @"(?ms)^function Add-UsersPauseAce.*?^\}").Value;
        Assert.NotEmpty(fn);
        // The LAN Fax printer's descriptor as Get-Printer -Full returned it on the dev PC (2026-09-30), shortened.
        const string before = "G:SYD:(A;;LCSWSDRCWDWO;;;S-1-5-21-1-2-3-1001)(A;OIIO;RPWPSDRCWDWO;;;S-1-5-21-1-2-3-1001)(A;OIIO;GA;;;CO)(A;;SWRC;;;WD)(A;CIIO;GX;;;WD)(A;;LCSWDTSDRCWDWO;;;BA)(A;OICIIO;GA;;;BA)";
        var tmp = Path.Combine(Path.GetTempPath(), "lanfax-sddl-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(tmp, fn + "\r\n$a = Add-UsersPauseAce '" + before + "'\r\n$b = Add-UsersPauseAce $a\r\n$a\r\n$b\r\n");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(ps51) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", tmp }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            Assert.True(p.ExitCode == 0, await stderr + await stdout);
            var lines = (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal(2, lines.Length);
            Assert.Equal(lines[0], lines[1]); // idempotent
            var after = new RawSecurityDescriptor(lines[0]);
            var original = new RawSecurityDescriptor(before);
            Assert.Equal(original.DiscretionaryAcl!.Count + 1, after.DiscretionaryAcl!.Count);
            var users = new SecurityIdentifier("S-1-5-32-545");
            var added = Assert.Single(after.DiscretionaryAcl.Cast<GenericAce>().OfType<CommonAce>(), a => a.SecurityIdentifier == users);
            Assert.Equal(AceQualifier.AccessAllowed, added.AceQualifier);
            Assert.Equal(0x0002000C, added.AccessMask); // READ_CONTROL | PRINTER_ACCESS_ADMINISTER | PRINTER_ACCESS_USE
            Assert.Equal(AceFlags.None, added.AceFlags); // the printer only, not its jobs
            var kept = after.DiscretionaryAcl.Cast<GenericAce>().Select(Bytes).ToList();
            foreach (GenericAce ace in original.DiscretionaryAcl)
                Assert.Contains(kept, k => k.SequenceEqual(Bytes(ace)));
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    private static byte[] Bytes(GenericAce a)
    {
        var b = new byte[a.BinaryLength];
        a.GetBinaryForm(b, 0);
        return b;
    }
}
