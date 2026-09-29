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
        Assert.Contains("'-DataDir'", t); // forwarded through the self-elevation relaunch
        Assert.DoesNotMatch(@"Set-Acl\s+-Path\s+\$SpoolDir", t);
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
}
