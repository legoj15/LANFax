using System.ComponentModel;
using System.Diagnostics;
using LANFax.App.Logic;
using LANFax.Core;

namespace LANFax.App.Tests;

public class NumberInputTests
{
    [Fact]
    public void Valid_ten_digit_is_normalized_and_sendable()
    {
        var s = NumberInput.Evaluate("(555) 234-5678");
        Assert.Equal("15552345678", s.Normalized);
        Assert.True(s.Allowed);
        Assert.Equal("", s.Reason);
    }

    [Fact]
    public void Empty_is_not_allowed_with_no_reason()
    {
        var s = NumberInput.Evaluate("");
        Assert.False(s.Allowed);
        Assert.Equal("", s.Reason); // nothing typed yet: no red text
    }

    [Theory]
    [InlineData("911")]
    [InlineData("1 900 555 1234")]
    [InlineData("12345")]
    public void Blocked_numbers_give_a_reason(string raw)
    {
        var s = NumberInput.Evaluate(raw);
        Assert.False(s.Allowed);
        Assert.False(string.IsNullOrEmpty(s.Reason));
    }
}

public class SendPlanTests
{
    static readonly Guid G = Guid.Parse("11111111-2222-3333-4444-555555555555");
    const string Key = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    [Fact]
    public void Checkbox_off_routes_document()
    {
        var s = new SendSession(G, @"C:\spool\jobs\x.pdf");
        var p = s.Build("555 234 5678", "hi", false, false);
        Assert.Equal(SendRoute.Document, p.Route);
        Assert.Equal("15552345678", p.Number);
        Assert.Equal("hi", p.Subject);
        // The key is a real GUID, not the spool file id, and the same for every plan of one session.
        Assert.True(Guid.TryParse(p.IdempotencyKey, out var k));
        Assert.NotEqual(G, k);
        Assert.Equal(p.IdempotencyKey, s.Build("555 234 5678", "hi", false, false).IdempotencyKey);
    }

    [Fact]
    public void Checkbox_on_routes_test_page_and_discards_pdf_regardless_of_keep()
    {
        var p = SendPlan.Create(G, @"C:\spool\jobs\x.pdf", "5552345678", null, true, true, Key);
        Assert.Equal(SendRoute.TestPage, p.Route);
        Assert.Equal(FileAction.Delete, p.AfterAccept);
        Assert.Equal(Key, p.IdempotencyKey);
    }

    [Fact]
    public void Keep_moves_to_sent_else_deletes()
    {
        var keep = SendPlan.Create(G, @"C:\spool\jobs\x.pdf", "5552345678", null, false, true, Key);
        Assert.Equal(FileAction.MoveToSent, keep.AfterAccept);
        Assert.Equal(Path.Combine(@"C:\spool", "sent", G + ".pdf"), keep.SentPath(@"C:\spool"));
        var del = SendPlan.Create(G, @"C:\spool\jobs\x.pdf", "5552345678", null, false, false, Key);
        Assert.Equal(FileAction.Delete, del.AfterAccept);
    }

    [Fact]
    public void Blank_subject_becomes_null()
    {
        Assert.Null(SendPlan.Create(G, "p", "5552345678", "  ", false, false, Key).Subject);
    }

    [Fact]
    public void Invalid_number_throws()
    {
        Assert.Throws<ArgumentException>(() => SendPlan.Create(G, "p", "911", null, false, false, Key));
    }
}

public class NotificationTextTests
{
    static Job J(string state, int? pages = null, string? err = null) =>
        new("id", "out", state, null, "15552345678", "15550001111", null, null, pages, err,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    [Fact]
    public void Sent_text() =>
        Assert.Equal("Fax to 15552345678 sent (3 pages)", NotificationText.ForTerminalJob(J("sent", 3)));

    [Fact]
    public void Sent_single_page() =>
        Assert.Equal("Fax to 15552345678 sent (1 page)", NotificationText.ForTerminalJob(J("sent", 1)));

    [Fact]
    public void Sent_unknown_pages_omits_count() =>
        Assert.Equal("Fax to 15552345678 sent", NotificationText.ForTerminalJob(J("sent")));

    [Fact]
    public void Failed_text_has_error() =>
        Assert.Equal("Fax to 15552345678 failed: no answer", NotificationText.ForTerminalJob(J("failed", null, "no answer")));

    [Fact]
    public void Failed_without_error() =>
        Assert.Equal("Fax to 15552345678 failed: unknown error", NotificationText.ForTerminalJob(J("failed")));

    [Fact]
    public void Failed_with_retry() =>
        Assert.Equal("Fax to 15552345678 failed: busy. Click here to retry.",
            NotificationText.ForFailedWithRetry(J("failed", null, "busy")));

    [Fact]
    public void Non_terminal_and_inbound_yield_null()
    {
        Assert.Null(NotificationText.ForTerminalJob(J("sending")));
        Assert.Null(NotificationText.ForTerminalJob(J("received")));
    }

    [Fact]
    public void Received_text() =>
        Assert.Equal("Fax received from 15550001111", NotificationText.ForReceived(J("received")));

    [Fact]
    public void Received_unknown_sender() =>
        Assert.Equal("Fax received from unknown sender",
            NotificationText.ForReceived(J("received") with { FromNumber = null }));

    [Fact]
    public void Queued_and_arm_text()
    {
        Assert.Equal("Fax to 15552345678 queued", NotificationText.Queued(J("queued"), "15552345678"));
        Assert.Equal("Fax to 15552345678 queued", NotificationText.Queued(J("queued") with { ToNumber = null }, "15552345678"));
        Assert.Equal("Server queued this fax to 15552345678 (you entered 15559990000)",
            NotificationText.Queued(J("queued"), "15559990000"));
        var until = new DateTimeOffset(2026, 9, 29, 14, 5, 0, TimeSpan.Zero);
        var expected = "Fax line will answer until " + until.ToLocalTime().ToString("t");
        Assert.Equal(expected, NotificationText.ReceiveArmed(until));
    }

    [Fact]
    public void Startup_notices()
    {
        Assert.Equal("The server rejected the LANFax token. Click here to open Settings.", NotificationText.Unauthorized);
        Assert.Equal("LANFax settings could not be read (config.json is not valid JSON). Click here to open Settings.",
            NotificationText.ConfigProblem("config.json is not valid JSON"));
    }
}

public class PrinterSetupTests
{
    sealed class FakeReader(params string[] keys) : IPrinterKeyReader
    {
        public bool Exists(string printerName) => keys.Contains(printerName, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Registry_key_path_for_a_printer()
    {
        Assert.Equal(@"SYSTEM\CurrentControlSet\Control\Print\Printers\LAN Fax", RegistryPrinterKeyReader.KeyPath("LAN Fax"));
        // A name with a backslash would address a different key; an empty one the parent. Both are "not installed".
        Assert.Null(RegistryPrinterKeyReader.KeyPath(@"..\Monitors"));
        Assert.Null(RegistryPrinterKeyReader.KeyPath(""));
        Assert.False(new RegistryPrinterKeyReader().Exists(""));
    }

    [Fact]
    public void Install_command_line_quotes_paths()
    {
        var a = PrinterSetup.BuildArguments(@"C:\Program Files\LANFax\install\Install-LANFaxPrinter.ps1", @"C:\ProgramData\LANFax\spool", "LAN Fax");
        Assert.Contains("-File \"C:\\Program Files\\LANFax\\install\\Install-LANFaxPrinter.ps1\"", a);
        Assert.Contains("-SpoolDir \"C:\\ProgramData\\LANFax\\spool\"", a);
        Assert.Contains("-PrinterName \"LAN Fax\"", a);
        Assert.Contains("-ExecutionPolicy Bypass", a);
    }

    [Fact]
    public void Trailing_backslash_is_trimmed_so_it_cannot_escape_the_quote()
    {
        var a = PrinterSetup.BuildArguments(@"C:\s.ps1", @"C:\ProgramData\LANFax\spool\\", "LAN Fax");
        Assert.Contains("-SpoolDir \"C:\\ProgramData\\LANFax\\spool\" ", a);
    }

    [Fact]
    public void Script_is_located_beside_the_app()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "install", "Install-LANFaxPrinter.ps1"), PrinterSetup.ScriptPath);
        Assert.True(File.Exists(PrinterSetup.ScriptPath), "the csproj must copy install\\*.ps1 to the output");
    }

    [Fact]
    public async Task Install_awaits_the_runner_then_rechecks()
    {
        var script = Path.GetTempFileName();
        try
        {
            bool installed = false;
            ProcessStartInfo? seen = null;
            var reader = new FuncReader(_ => installed);
            var ps = new PrinterSetup(reader, async psi => { seen = psi; await Task.Yield(); installed = true; });
            Assert.Null(await ps.InstallAsync(@"C:\spool", "LAN Fax", script));
            Assert.Equal("runas", seen!.Verb);

            var cancelled = new PrinterSetup(reader, _ => throw new Win32Exception(1223));
            Assert.Equal("Installation cancelled.", await cancelled.InstallAsync(@"C:\spool", "LAN Fax", script));

            installed = false;
            var noop = new PrinterSetup(reader, _ => Task.CompletedTask);
            Assert.StartsWith("The printer was not installed", await noop.InstallAsync(@"C:\spool", "LAN Fax", script));
        }
        finally { File.Delete(script); }
    }

    [Fact]
    public async Task Install_reports_missing_script()
    {
        var ps = new PrinterSetup(new FakeReader(), _ => throw new InvalidOperationException("must not run"));
        Assert.StartsWith("Install script not found", await ps.InstallAsync(@"C:\spool", "LAN Fax", @"C:\nope\x.ps1"));
    }

    sealed class FuncReader(Func<string, bool> f) : IPrinterKeyReader
    {
        public bool Exists(string printerName) => f(printerName);
    }
}

public class ConfigLocationTests
{
    [Fact]
    public void Uses_primary_when_writable_else_fallback()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lanfax-cl-" + Guid.NewGuid().ToString("N"));
        try
        {
            var primary = Path.Combine(dir, "a", "config.json");
            var fallback = Path.Combine(dir, "b", "config.json");
            var r1 = ConfigLocation.Choose(primary, fallback);
            Assert.Equal(primary, r1.Path);
            Assert.False(r1.IsFallback);
            // primary directory impossible (a file sits where the directory must be)
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "blocked"), "x");
            var r2 = ConfigLocation.Choose(Path.Combine(dir, "blocked", "config.json"), fallback);
            Assert.Equal(fallback, r2.Path);
            Assert.True(r2.IsFallback);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Keeps_reading_the_machine_file_until_a_fallback_exists()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lanfax-cl-" + Guid.NewGuid().ToString("N"));
        try
        {
            var primary = Path.Combine(dir, "a", "config.json");
            var fallback = Path.Combine(dir, "b", "config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(primary)!);
            File.WriteAllText(primary, "{}");
            Func<string, bool> readOnly = _ => false; // ProgramData exists but is not writable for this user

            var before = ConfigLocation.Choose(primary, fallback, readOnly);
            Assert.True(before.IsFallback);
            Assert.Equal(fallback, before.Path);    // saves go to the profile
            Assert.Equal(primary, before.ReadPath); // but the admin-written file is still read

            Directory.CreateDirectory(Path.GetDirectoryName(fallback)!);
            File.WriteAllText(fallback, "{}");
            Assert.Equal(fallback, ConfigLocation.Choose(primary, fallback, readOnly).ReadPath);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public class InstallerScriptTests
{
    static string IssPath()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "install", "LANFax.iss"))) d = d.Parent;
        return Path.Combine(d!.FullName, "install", "LANFax.iss");
    }

    static string Iss() => File.ReadAllText(IssPath());

    /// <summary>The non-comment lines of one [Section].</summary>
    static List<string> Section(string name)
    {
        var lines = new List<string>();
        bool inside = false;
        foreach (var raw in File.ReadAllLines(IssPath()))
        {
            var l = raw.Trim();
            if (l.StartsWith('[') && l.EndsWith(']')) { inside = l == "[" + name + "]"; continue; }
            if (inside && l.Length > 0 && !l.StartsWith(';')) lines.Add(l);
        }
        return lines;
    }

    [Fact]
    public void Printer_script_exit_code_is_checked_with_a_non_fatal_message()
    {
        var t = Iss();
        Assert.Contains("Install-LANFaxPrinter.ps1", t);
        Assert.Matches(@"Exec\(.*powershell\.exe", t);
        Assert.Contains("ResultCode", t);
        Assert.Contains("SuppressibleMsgBox", t);
        Assert.DoesNotMatch(@"(?m)^Filename:.*Install-LANFaxPrinter", t);
    }

    [Fact]
    public void Uninstall_stops_the_running_app_in_the_uninstall_step_not_at_initialization()
    {
        var t = Iss();
        // Killing in InitializeUninstall would stop the app even when the user then declines the uninstall.
        Assert.DoesNotContain("InitializeUninstall", t);
        var m = System.Text.RegularExpressions.Regex.Match(t,
            @"procedure CurUninstallStepChanged\(CurUninstallStep: TUninstallStep\);(?<body>.*?)\r?\nend;",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(m.Success, "CurUninstallStepChanged missing");
        Assert.Matches(@"(?s)usUninstall.*taskkill.*\{#AppExe\}", m.Groups["body"].Value);
    }

    [Fact]
    public void Shared_data_dir_is_users_modify()
    {
        var dirs = Section("Dirs");
        Assert.Contains(@"Name: ""{commonappdata}\LANFax""; Permissions: users-modify; Flags: uninsneveruninstall", dirs);
    }

    [Fact]
    public void Uninstall_keeps_the_data_dirs()
    {
        // config.json, unsent documents (spool\jobs) and kept copies must survive an uninstall.
        var dirs = Section("Dirs");
        Assert.Equal(3, dirs.Count);
        Assert.All(dirs, d => Assert.EndsWith("; Flags: uninsneveruninstall", d));
        Assert.Contains(dirs, d => d.Contains(@"{commonappdata}\LANFax\spool\jobs"""));
    }

    [Fact]
    public async Task Script_compiles_with_inno_setup_when_installed()
    {
        const string iscc = @"E:\Program Files (x86)\Inno Setup 6\ISCC.exe";
        if (!File.Exists(iscc)) return; // Inno Setup is not on this machine: the static checks above still run
        var tmp = Path.Combine(Path.GetTempPath(), "lanfax-iscc-" + Guid.NewGuid().ToString("N"));
        var stub = Path.Combine(tmp, "app");
        var output = Path.Combine(tmp, "out");
        Directory.CreateDirectory(stub);
        try
        {
            File.WriteAllText(Path.Combine(stub, "LANFax.exe"), "stub"); // [Files] needs the published app; a stub is enough to compile
            var psi = new ProcessStartInfo(iscc)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            psi.ArgumentList.Add("/Q");
            psi.ArgumentList.Add("/O" + output);
            psi.ArgumentList.Add("/DAppSource=" + stub);
            psi.ArgumentList.Add(IssPath());
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await p.WaitForExitAsync(cts.Token);
            Assert.True(p.ExitCode == 0, $"ISCC exit {p.ExitCode}: {await stderr} {await stdout}");
            Assert.True(File.Exists(Path.Combine(output, "LANFax-Setup.exe")));
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }
}
