using System.Text.RegularExpressions;
using LANFax.Core;

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
                // [CmdletBinding()] as in the real scripts: an argument the driver does not declare fails binding.
                "[CmdletBinding()] param([string]$DataDir, [string]$SpoolDir, [string]$PrinterName, [string]$PipeName)",
                "if ($env:LANFAX_PROBE_CHILD) { Set-Content -LiteralPath $env:LANFAX_PROBE_OUT -Encoding UTF8 -Value @($DataDir, $SpoolDir, $PrinterName, $PipeName); exit 0 }",
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
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", driver, "-SpoolDir", spoolDir, "-PrinterName", "LAN Fax", "-PipeName", "LAN Fax Pipe" })
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
            Assert.Equal(new[] { hasDataDir ? dataDir : "", spoolDir, "LAN Fax", "LAN Fax Pipe" }, got);
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

    // ---- the pipe port (2026-09-30): every print job is its own connection to \\.\pipe\LANFax ----

    private static string Script(string name) => File.ReadAllText(Path.Combine(Dir(), name));

    [Theory]
    [InlineData("Install-LANFaxPrinter.ps1")]
    [InlineData("Uninstall-LANFaxPrinter.ps1")]
    public void ScriptsUseThePipePortNamedByPipeName(string script)
    {
        var t = Script(script);
        Assert.Matches(@"\[string\]\$PipeName\s*=\s*'LANFax'", t);
        Assert.Contains(@"$portName = '\\.\pipe\' + $PipeName", t);
        Assert.Contains("-PipeName $(ConvertTo-QuotedArg $PipeName)", t); // forwarded through the self-elevation relaunch
        Assert.Contains("$PipeName", t[t.IndexOf("foreach ($value in", StringComparison.Ordinal)..t.IndexOf("$isAdmin", StringComparison.Ordinal)]);
    }

    [Fact]
    public void InstallScriptRemovesTheOldFilePortOnlyWhenNoPrinterUsesIt()
    {
        var t = Script("Install-LANFaxPrinter.ps1");
        Assert.Matches(@"\$oldPort\s*=\s*Join-Path\s+\$SpoolDir\s+'print\.pdf'", t);
        Assert.Matches(@"Where-Object\s+PortName\s+-eq\s+\$oldPort", t);
        var remove = t.IndexOf("Remove-PrinterPort -Name $oldPort", StringComparison.Ordinal);
        Assert.True(remove > t.IndexOf("Set-Printer -Name $PrinterName -PortName $portName", StringComparison.Ordinal),
            "the printer must leave the old port before it is removed");
    }

    [Fact]
    public void InstallScriptTurnsKeepPrintedJobsOffAndNeverPauses()
    {
        var t = Script("Install-LANFaxPrinter.ps1");
        Assert.Matches(@"\.KeepPrintedJobs\s*=\s*\$false", t);
        Assert.DoesNotMatch(@"\.KeepPrintedJobs\s*=\s*\$true", t);
        Assert.Contains("-MethodName Resume", t); // a queue the superseded install left paused prints again
        Assert.DoesNotContain("-MethodName Pause", t);
    }

    [Fact]
    public void InstallScriptAddsNoPermissionsAndNoFileAccessForThePrinter()
    {
        var t = Script("Install-LANFaxPrinter.ps1");
        Assert.DoesNotContain("InsertAce", t);
        Assert.DoesNotContain("AddAccessRule", t);
        Assert.DoesNotContain("Get-Process -Name LANFax", t);
        // The only permission change is taking back the superseded Users pause ACE, and only when it is there.
        Assert.Matches(@"(?s)if\s*\(\s*\$withoutAce\s+-ne\s+\$sddl\s*\)\s*\{\s*Set-Printer\s+-Name\s+\$PrinterName\s+-PermissionSDDL\s+\$withoutAce", t);
    }

    [Fact]
    public void UninstallScriptRemovesThePipePortAndAnUnusedOldFilePort()
    {
        var t = Script("Uninstall-LANFaxPrinter.ps1");
        Assert.Contains("Remove-PrinterPort -Name $portName", t);
        Assert.Contains("Remove-PrinterPort -Name $oldPort", t);
        Assert.Matches(@"Where-Object\s+PortName\s+-eq\s+\$oldPort", t);
        Assert.DoesNotContain("-MethodName", t);
    }

    [Fact]
    public void UninstallScriptEmptiesTheQueueBeforeRemovingThePrinter()
    {
        // With no listener a waiting job never finishes, and a printer with jobs is only marked "pending deletion":
        // the printer and the pipe port would stay behind. Purge first, then check the printer really went.
        var t = Script("Uninstall-LANFaxPrinter.ps1");
        var purge = t.IndexOf("Remove-PrintJob", StringComparison.Ordinal);
        var remove = t.IndexOf("Remove-Printer -Name $PrinterName", StringComparison.Ordinal);
        Assert.True(purge >= 0 && remove > purge, "jobs must be removed before the printer");
        Assert.Matches(@"(?s)Remove-Printer -Name \$PrinterName.*Get-Printer -Name \$PrinterName", t);
        Assert.DoesNotContain("go with the printer", t);
    }

    [Fact]
    public void InstallScriptMigrationStepsCannotStopTheOthers()
    {
        // A failing cleanup (old port still open, an unusual descriptor) must not leave the printer paused with
        // "Keep printed documents" on: the queue settings come first, the ACE and the old port are warnings.
        var t = Script("Install-LANFaxPrinter.ps1");
        int keep = t.IndexOf(".KeepPrintedJobs = $false", StringComparison.Ordinal);
        int resume = t.IndexOf("-MethodName Resume", StringComparison.Ordinal);
        int ace = t.IndexOf("-PermissionSDDL $withoutAce", StringComparison.Ordinal);
        int oldPort = t.IndexOf("Remove-PrinterPort -Name $oldPort", StringComparison.Ordinal);
        Assert.True(keep > 0 && resume > keep && ace > resume && oldPort > ace, $"order keep {keep}, resume {resume}, ace {ace}, old port {oldPort}");
        Assert.Matches(@"(?s)try\s*\{[^}]*-PermissionSDDL \$withoutAce[^}]*\}\s*\}?\s*catch", t);
        Assert.Matches(@"(?s)try\s*\{[^}]*Remove-PrinterPort -Name \$oldPort[^}]*\}\s*\}?\s*catch", t);
    }

    [Fact]
    public void InstallScriptListsJobsTheSupersededInstallKeptAfterPrintingAndLeavesThem()
    {
        // KeepPrintedJobs goes off; jobs already kept as printed are harmless (they are never printed again unless
        // someone restarts one), so they are listed for the user to delete, never removed by the script.
        var t = Script("Install-LANFaxPrinter.ps1");
        Assert.DoesNotContain("Remove-PrintJob", t);
        // Listed BEFORE "Keep printed documents" goes off: turning it off may let the spooler drop the kept jobs.
        Assert.Matches(@"(?s)Format-KeptPrintedJobs \(Get-PrintJob -PrinterName \$PrinterName\).*\.KeepPrintedJobs = \$false", t);
        Assert.Matches(@"(?s)try\s*\{[^}]*Format-KeptPrintedJobs \(Get-PrintJob[^}]*\}\s*catch", t);
    }

    [Fact]
    public async Task KeptPrintedJobsAreCountedAndNamedAndErrorJobsAreNot()
    {
        var ps51 = Ps51();
        if (ps51 == null) return;
        var (code, output) = await RunFunction(ps51, "Format-KeptPrintedJobs",
            "function J($n, $s) { [pscustomobject]@{ DocumentName = $n; JobStatus = $s } }",
            "Format-KeptPrintedJobs @((J 'Invoice.pdf' 'Printed, Retained'), (J 'Letter' 'Error, Printed'), (J 'Memo' 'Printed'), (J 'Spooling' 'Spooling'))",
            "'---'",
            "Format-KeptPrintedJobs @((J 'Queued' 'Normal'))",
            "'---'",
            "Format-KeptPrintedJobs @()");
        Assert.True(code == 0, output);
        var parts = output.Replace("\r", "").Split("---\n");
        Assert.Equal(3, parts.Length);
        Assert.Contains("2 printed document(s)", parts[0]);
        Assert.Contains("'Invoice.pdf'", parts[0]);
        Assert.Contains("'Memo'", parts[0]);
        Assert.DoesNotContain("Letter", parts[0]); // in Error: never delivered, not a kept printed job
        Assert.DoesNotContain("Spooling", parts[0]);
        Assert.Equal("", parts[1].Trim());
        Assert.Equal("", parts[2].Trim());
    }

    [Fact]
    public void InstallScriptSortsADocumentLeftAtTheOldFilePortIntoJobsOrRejected()
    {
        // A document printed to the old file port while the tray was not running sits in spool\print.pdf. A single
        // complete PDF moves to spool\jobs\<guid>.pdf (offered as unsent at the tray's start); anything else, such as
        // two jobs written over each other, goes to spool\rejected\ and is never offered.
        var t = Script("Install-LANFaxPrinter.ps1");
        Assert.Matches(@"(?s)Test-Path -LiteralPath \$oldPort.*if \(Test-SinglePdf \$oldPort\).*Move-Item -LiteralPath \$oldPort -Destination \(Join-Path \$SpoolDir ""jobs\\\$\(\[guid\]::NewGuid\(\)\)\.pdf""\).*\$dest = Join-Path \$rejectedDir.*Move-Item -LiteralPath \$oldPort -Destination \$dest", t);
        Assert.True(t.IndexOf("Move-Item -LiteralPath $oldPort", StringComparison.Ordinal) > t.IndexOf("Set-Printer -Name $PrinterName -PortName $portName", StringComparison.Ordinal),
            "only once the printer no longer writes there");
        Assert.Matches(@"(?s)try\s*\{.*Move-Item -LiteralPath \$oldPort.*\}\s*catch \{ ""warning: moving the document left in", t);
    }

    [Fact]
    public async Task TestSinglePdfAppliesTheListenersAcceptanceRule()
    {
        // Same vectors as PrintPipeListener (PrintPipeListenerTests): a leftover the migration offers as unsent is
        // exactly what the listener would have raised. Two jobs written over each other hold two %%EOF and are refused.
        var ps51 = Ps51();
        if (ps51 == null) return;
        var dir = Path.Combine(Path.GetTempPath(), "lanfax-leftover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cases = PdfAcceptanceVectors.Cases;
            foreach (var c in cases) File.WriteAllBytes(Path.Combine(dir, c.name + ".pdf"), c.bytes);
            var calls = cases.Select(c => $"Test-SinglePdf '{Path.Combine(dir, c.name + ".pdf").Replace("'", "''")}' {PdfAcceptanceVectors.MaxBytes}").ToArray();
            var (code, output) = await RunFunction(ps51, "Test-SinglePdf", calls);
            Assert.True(code == 0, output);
            var got = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal(cases.Select(c => $"{c.name}={(c.accepted ? "True" : "False")}"), cases.Select((c, i) => $"{c.name}={(i < got.Length ? got[i] : "?")}"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void TestSinglePdfDefaultsToTheListenersSizeLimit()
    {
        var t = Script("Install-LANFaxPrinter.ps1");
        Assert.Matches(@"function Test-SinglePdf\(\[string\]\$Path, \[long\]\$MaxBytes = " + (PrintPipeListener.DefaultMaxJobBytes / (1024 * 1024)) + @"MB\)", t);
    }

    private static string? Ps51()
    {
        var ps51 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        return File.Exists(ps51) ? ps51 : null;
    }

    /// <summary>Runs one function of the install script, cut out of it, followed by <paramref name="lines"/>.</summary>
    private static async Task<(int code, string output)> RunFunction(string ps51, string function, params string[] lines)
    {
        var fn = Regex.Match(Script("Install-LANFaxPrinter.ps1"), @"(?ms)^function " + function + @"\b.*?^\}").Value;
        Assert.NotEmpty(fn);
        var tmp = Path.Combine(Path.GetTempPath(), "lanfax-fn-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(tmp, fn + "\r\n" + string.Join("\r\n", lines) + "\r\n");
        try { return await RunPs51(ps51, null, tmp); }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Theory]
    [InlineData("Install-LANFaxPrinter.ps1")]
    [InlineData("Uninstall-LANFaxPrinter.ps1")]
    public async Task ScriptsParseOnWindowsPowerShell51(string script)
    {
        var ps51 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(ps51)) return;
        var path = Path.Combine(Dir(), script);
        Assert.All(File.ReadAllBytes(path), b => Assert.True(b < 0x80, "non-ASCII byte: 5.1 reads a BOM-less script as ANSI"));
        var (code, output) = await RunPs51(ps51,
            "$e = $null; $null = [System.Management.Automation.Language.Parser]::ParseFile('" + path.Replace("'", "''") + "', [ref]$null, [ref]$e); " +
            "$e | ForEach-Object { $_.Message }; exit $e.Count");
        Assert.True(code == 0, output);
    }

    [Fact]
    public async Task SupersededPauseAceIsRemovedExactlyAndEveryOtherAceIsKept()
    {
        var ps51 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(ps51)) return;
        var fn = System.Text.RegularExpressions.Regex.Match(Script("Install-LANFaxPrinter.ps1"), @"(?ms)^function Remove-UsersPauseAce.*?^\}").Value;
        Assert.NotEmpty(fn);
        // The LAN Fax printer's descriptor on the dev PC (2026-09-30), shortened: the superseded install's ACE first.
        const string others = "(A;;LCSWSDRCWDWO;;;S-1-5-21-1-2-3-1001)(A;OIIO;GA;;;CO)(A;;SWRC;;;WD)(A;CIIO;GX;;;WD)(A;;LCSWDTSDRCWDWO;;;BA)(A;OICIIO;GA;;;BA)";
        const string with = "G:SYD:(A;;LCSWRC;;;BU)" + others;
        const string usersPrint = "G:SYD:(A;;SWRC;;;BU)" + others; // Users with print rights only: not ours, kept
        var tmp = Path.Combine(Path.GetTempPath(), "lanfax-sddl-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(tmp, fn + "\r\n$a = Remove-UsersPauseAce '" + with + "'\r\n$a\r\nRemove-UsersPauseAce $a\r\nRemove-UsersPauseAce '" + usersPrint + "'\r\n");
        try
        {
            var (code, output) = await RunPs51(ps51, null, tmp);
            Assert.True(code == 0, output);
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal(3, lines.Length);
            Assert.Equal(lines[0], lines[1]); // idempotent
            Assert.Equal(Canon("G:SYD:" + others), Canon(lines[0]));
            Assert.Equal(Canon(usersPrint), Canon(lines[2]));
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    private static string Canon(string sddl) =>
        new System.Security.AccessControl.RawSecurityDescriptor(sddl).GetSddlForm(System.Security.AccessControl.AccessControlSections.All);

    private static async Task<(int code, string output)> RunPs51(string ps51, string? command, string? file = null)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(ps51) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass" }) psi.ArgumentList.Add(a);
        if (file != null) { psi.ArgumentList.Add("-File"); psi.ArgumentList.Add(file); }
        else { psi.ArgumentList.Add("-Command"); psi.ArgumentList.Add(command!); }
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await p.WaitForExitAsync(cts.Token);
        return (p.ExitCode, await stdout + await stderr);
    }
}
