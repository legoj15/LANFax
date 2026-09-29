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
