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
        public string? Port(string printerName) => Exists(printerName) ? @"\\.\pipe\LANFax" : null;
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

    private const string Out = @"C:\Users\me\AppData\Local\Temp\lanfax-install.log";

    [Fact]
    public void Install_command_runs_the_script_with_quoted_values_and_captures_its_output()
    {
        var c = PrinterSetup.BuildCommand(@"C:\Program Files\LANFax\install\Install-LANFaxPrinter.ps1", @"C:\ProgramData\LANFax\spool", "LAN Fax", Out);
        Assert.Contains(@"& 'C:\Program Files\LANFax\install\Install-LANFaxPrinter.ps1' -SpoolDir 'C:\ProgramData\LANFax\spool' -PrinterName 'LAN Fax' *>&1", c);
        Assert.Contains($"$out = '{Out}'", c);
        var a = PrinterSetup.BuildArguments(@"C:\s.ps1", @"C:\spool", "LAN Fax", Out);
        Assert.StartsWith("-NoProfile -ExecutionPolicy Bypass -NonInteractive -EncodedCommand ", a);
        var b64 = a[(a.LastIndexOf(' ') + 1)..];
        Assert.Equal(PrinterSetup.BuildCommand(@"C:\s.ps1", @"C:\spool", "LAN Fax", Out), System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(b64)));
    }

    [Fact]
    public void Trailing_backslash_is_trimmed_and_single_quotes_are_doubled()
    {
        Assert.Contains(@"-SpoolDir 'C:\ProgramData\LANFax\spool' ", PrinterSetup.BuildCommand(@"C:\s.ps1", @"C:\ProgramData\LANFax\spool\\", "LAN Fax", Out));
        Assert.Contains(@"-SpoolDir 'D:\' ", PrinterSetup.BuildCommand(@"C:\s.ps1", @"D:\", "LAN Fax", Out));
        // PowerShell ends a single-quoted string at any of these quote characters; doubled, each is literal.
        Assert.Contains("-PrinterName 'O''Brien \u2019\u2019s Fax'", PrinterSetup.BuildCommand(@"C:\s.ps1", @"C:\spool", "O'Brien \u2019s Fax", Out));
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
            var ps = new PrinterSetup(reader, async (psi, _) => { seen = psi; await Task.Yield(); installed = true; return 0; });
            var ok = await ps.InstallAsync(@"C:\spool", "LAN Fax", script);
            Assert.Null(ok.Error);
            Assert.Empty(ok.Warnings);
            Assert.Equal("runas", seen!.Verb); // still elevated through UAC
            Assert.True(seen.UseShellExecute);

            var cancelled = new PrinterSetup(reader, (_, _) => throw new Win32Exception(1223));
            Assert.Equal("Installation cancelled.", (await cancelled.InstallAsync(@"C:\spool", "LAN Fax", script)).Error);

            installed = false;
            var noop = new PrinterSetup(reader, (_, _) => Task.FromResult(0));
            Assert.StartsWith("The printer was not installed", (await noop.InstallAsync(@"C:\spool", "LAN Fax", script)).Error);
        }
        finally { File.Delete(script); }
    }

    [Fact]
    public async Task Install_reports_missing_script()
    {
        var ps = new PrinterSetup(new FakeReader(), (_, _) => throw new InvalidOperationException("must not run"));
        Assert.StartsWith("Install script not found", (await ps.InstallAsync(@"C:\spool", "LAN Fax", @"C:\nope\x.ps1")).Error);
    }

    [Fact]
    public async Task Install_returns_the_script_output_and_its_warnings_and_removes_the_output_file()
    {
        // The rescue of a leftover print.pdf is a warning-only step: its warning must reach the tray, not vanish.
        var script = Path.GetTempFileName();
        string? outPath = null;
        const string warning = @"warning: moving the document left in 'C:\spool\print.pdf' out of the way failed: locked";
        try
        {
            var ps = new PrinterSetup(new FakeReader("LAN Fax"), (_, o) =>
            {
                outPath = o;
                File.WriteAllLines(o, ["moving printer 'LAN Fax'", warning, @"LANFax printer 'LAN Fax' -> \\.\pipe\LANFax"]);
                return Task.FromResult(0);
            });
            var r = await ps.InstallAsync(@"C:\spool", "LAN Fax", script);
            Assert.Null(r.Error);
            Assert.Equal(3, r.Output.Count);
            Assert.Equal(warning, Assert.Single(r.Warnings));
            Assert.False(File.Exists(outPath));
        }
        finally { File.Delete(script); }
    }

    [Fact]
    public async Task Install_fails_on_a_nonzero_exit_even_when_the_port_reads_right()
    {
        var script = Path.GetTempFileName();
        try
        {
            var ps = new PrinterSetup(new FakeReader("LAN Fax"), (_, o) =>
            {
                File.WriteAllLines(o, ["moving printer 'LAN Fax'", "error: Access is denied"]);
                return Task.FromResult(1);
            });
            var r = await ps.InstallAsync(@"C:\spool", "LAN Fax", script);
            Assert.Equal("The printer install script failed (exit code 1): error: Access is denied", r.Error);
            var silent = new PrinterSetup(new FakeReader("LAN Fax"), (_, _) => Task.FromResult(3));
            Assert.Equal("The printer install script failed (exit code 3).", (await silent.InstallAsync(@"C:\spool", "LAN Fax", script)).Error);
        }
        finally { File.Delete(script); }
    }

    [Fact]
    public async Task Install_command_captures_warnings_errors_and_the_exit_code_on_windows_powershell()
    {
        // The real wrapper, minus elevation: the script's output lands in the file even when the script throws.
        var ps51 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(ps51)) return;
        var dir = Path.Combine(Path.GetTempPath(), "lanfax install cmd " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var script = Path.Combine(dir, "fake 'install'.ps1");
            File.WriteAllText(script, string.Join("\r\n",
                "[CmdletBinding()] param([string]$SpoolDir, [string]$PrinterName)",
                "$ErrorActionPreference = 'Stop'",
                "\"spool=$SpoolDir printer=$PrinterName\"",
                "\"warning: something failed\"",
                "if ($PrinterName -eq 'Throw') { throw 'boom' }",
                "if ($PrinterName -eq 'Native') { cmd /c exit 3 }", // a native command the script tolerates: not a failure
                "if ($PrinterName -eq 'Exit') { exit 4 }",
                ""));
            async Task<(int code, string[] lines)> Run(string printer)
            {
                var o = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".log");
                var psi = new ProcessStartInfo(ps51, PrinterSetup.BuildArguments(script, @"C:\sp ool\", printer, o)) { UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(psi)!;
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                await p.WaitForExitAsync(cts.Token);
                return (p.ExitCode, File.Exists(o) ? File.ReadAllLines(o) : []);
            }
            var ok = await Run("LAN 'Fax'");
            Assert.Equal(0, ok.code);
            Assert.Equal([@"spool=C:\sp ool printer=LAN 'Fax'", "warning: something failed"], ok.lines);
            var bad = await Run("Throw");
            Assert.Equal(1, bad.code);
            Assert.Equal("warning: something failed", bad.lines[1]);
            Assert.Equal("error: boom", bad.lines[^1]);
            Assert.Equal(0, (await Run("Native")).code);
            var exit = await Run("Exit");
            Assert.Equal(4, exit.code); // the script's own explicit exit code is kept
            Assert.Equal("warning: something failed", exit.lines[^1]);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    sealed class FuncReader(Func<string, bool> f) : IPrinterKeyReader
    {
        public bool Exists(string printerName) => f(printerName);
        public string? Port(string printerName) => f(printerName) ? @"\\.\pipe\LANFax" : null;
    }

    sealed class PortReader(string? port) : IPrinterKeyReader
    {
        public bool Exists(string printerName) => port != null;
        public string? Port(string printerName) => port;
    }

    [Theory]
    [InlineData(null, PrinterState.Missing)]
    [InlineData(@"\\.\pipe\LANFax", PrinterState.Installed)]
    [InlineData(@"\\.\PIPE\lanfax", PrinterState.Installed)]
    // The file port of installs before 2026-09-30: the tray offers the repair, which moves the printer to the pipe.
    [InlineData(@"C:\ProgramData\LANFax\spool\print.pdf", PrinterState.Misconfigured)]
    [InlineData(@"\\.\pipe\LANFaxProbe", PrinterState.Misconfigured)]
    [InlineData(@"\\otherpc\pipe\LANFax", PrinterState.Misconfigured)]
    [InlineData("PORTPROMPT:", PrinterState.Misconfigured)]
    [InlineData("", PrinterState.Misconfigured)]
    public void Status_compares_the_printer_port_with_the_pipe(string? port, PrinterState expected)
    {
        var st = new PrinterSetup(new PortReader(port)).Status("LAN Fax");
        Assert.Equal(expected, st.State);
        Assert.Equal(port, st.ActualPort);
    }

    [Fact]
    public void Expected_port_is_the_lanfax_pipe() =>
        Assert.Equal(@"\\.\pipe\LANFax", PrinterSetup.ExpectedPort());

    [Fact]
    public void Print_pipe_problem_names_the_printer_and_fits_a_balloon()
    {
        Assert.Equal(@"""LAN Fax"" printer: a print job was rejected: it is not a PDF (3 bytes). Kept as C:\r\x.pdf",
            NotificationText.PrintPipeProblem("LAN Fax", @"a print job was rejected: it is not a PDF (3 bytes). Kept as C:\r\x.pdf"));
        var longText = NotificationText.PrintPipeProblem("LAN Fax", new string('x', 400));
        Assert.Equal(255, longText.Length);
        Assert.EndsWith("...", longText);
    }

    [Fact]
    public void Misconfigured_notice_names_the_port() =>
        Assert.Equal(@"The ""LAN Fax"" printer prints to C:\Old\print.pdf, not to LANFax (\\.\pipe\LANFax). Click here to fix it.",
            NotificationText.PrinterMisconfigured("LAN Fax", @"C:\Old\print.pdf"));

    [Fact]
    public void Stuck_print_jobs_notice_counts_says_how_to_send_them_and_warns_of_a_double_delivery()
    {
        // A restarted job was once delivered twice, and two prints of the same document within the same second are
        // byte-identical (measured 2026-09-30), so duplicates cannot be told from reprints: the notice warns instead.
        var one = NotificationText.StuckPrintJobs("LAN Fax", 1);
        var three = NotificationText.StuckPrintJobs("LAN Fax", 3);
        Assert.Equal(@"1 document printed to ""LAN Fax"" before LANFax started is stuck in the printer queue with an error. Click here to open the queue and restart it. A restart can deliver it twice: send only one copy.",
            one);
        Assert.Equal(@"3 documents printed to ""LAN Fax"" before LANFax started are stuck in the printer queue with an error. Click here to open the queue and restart them. A restart can deliver a document twice: send only one copy.",
            three);
        Assert.True(one.Length <= 255 && three.Length <= 255); // the balloon's limit
    }

    [Fact]
    public async Task Install_reports_a_printer_still_on_the_wrong_port()
    {
        var script = Path.GetTempFileName();
        try
        {
            var ps = new PrinterSetup(new PortReader(@"C:\Old\print.pdf"), (_, _) => Task.FromResult(0));
            Assert.StartsWith(@"The printer still points at C:\Old\print.pdf", (await ps.InstallAsync(@"C:\spool", "LAN Fax", script)).Error);
        }
        finally { File.Delete(script); }
    }

    [Fact]
    public void Missing_notice_names_the_printer() =>
        Assert.Equal(@"The ""LAN Fax"" printer is not installed. Click here to install it.", NotificationText.PrinterMissing("LAN Fax"));
}

public class ListenerWatchTests
{
    [Fact]
    public void A_listener_that_is_not_running_is_restarted_and_the_first_restart_is_announced()
    {
        var w = new ListenerWatch();
        Assert.Equal(ListenerAction.None, w.Observe(running: true));
        Assert.Equal(ListenerAction.RestartAndAnnounce, w.Observe(running: false));
        Assert.Equal(ListenerAction.None, w.Observe(running: true));
        Assert.Equal(ListenerAction.Restart, w.Observe(running: false)); // one balloon per session; later ones only logged
    }

    [Fact]
    public void A_listener_stopped_on_purpose_is_never_restarted()
    {
        // Session end (WM_ENDSESSION) and Exit stop the listener deliberately; a 30 s tick after that must not
        // start it again (the session-end stop does not set the tray's _exiting).
        var w = new ListenerWatch();
        Assert.Equal(ListenerAction.None, w.Observe(running: true));
        w.StoppedOnPurpose();
        Assert.Equal(ListenerAction.None, w.Observe(running: false));
        Assert.Equal(ListenerAction.None, w.Observe(running: false));
    }

    [Fact]
    public void Install_notice_names_the_first_warning_and_fits_a_balloon()
    {
        Assert.Equal(@"The ""LAN Fax"" printer is installed and prints to \\.\pipe\LANFax.", NotificationText.PrinterInstalled("LAN Fax", []));
        Assert.Equal(@"The ""LAN Fax"" printer is installed, with a warning (see the log): warning: x",
            NotificationText.PrinterInstalled("LAN Fax", ["warning: x"]));
        var two = NotificationText.PrinterInstalled("LAN Fax", ["warning: " + new string('y', 300), "warning: z"]);
        Assert.StartsWith(@"The ""LAN Fax"" printer is installed, with 2 warnings (see the log): warning: yyy", two);
        Assert.Equal(255, two.Length);
    }

    [Fact]
    public void Leftover_notices_say_where_the_document_is()
    {
        Assert.Equal(@"A document printed before LANFax was updated is still in C:\s\print.pdf and could not be moved (locked). Close any program that has it open; LANFax tries again at its next start.",
            NotificationText.LeftoverStuck(@"C:\s\print.pdf", "locked"));
        Assert.Equal(@"A document printed before LANFax was updated was not one complete PDF and is not offered for sending; it was moved to C:\s\rejected\print-1.pdf.",
            NotificationText.LeftoverRejected(@"C:\s\rejected\print-1.pdf"));
    }

    [Fact]
    public void Listener_restart_notice_names_the_printer() =>
        Assert.Equal(@"Receiving from the ""LAN Fax"" printer had stopped; LANFax started it again. If this repeats, restart LANFax.",
            NotificationText.ListenerRestarted("LAN Fax"));
}

public class PrinterWatchTests
{
    private static readonly PrinterStatus Ok = new(PrinterState.Installed, @"\\.\pipe\LANFax");
    private static readonly PrinterStatus Gone = new(PrinterState.Missing, null);
    private static readonly PrinterStatus Elsewhere = new(PrinterState.Misconfigured, "PORTPROMPT:");

    [Fact]
    public void A_steady_installed_printer_is_never_announced()
    {
        var w = new PrinterWatch(Ok);
        for (int i = 0; i < 5; i++) Assert.Null(w.Observe(Ok));
    }

    [Fact]
    public void Installed_to_missing_is_announced_once_not_every_tick()
    {
        var w = new PrinterWatch(Ok);
        Assert.Equal(Gone, w.Observe(Gone));
        Assert.Null(w.Observe(Gone));
        Assert.Null(w.Observe(Gone));
    }

    [Fact]
    public void Installed_to_misconfigured_is_announced_once()
    {
        var w = new PrinterWatch(Ok);
        Assert.Equal(Elsewhere, w.Observe(Elsewhere));
        Assert.Null(w.Observe(Elsewhere));
    }

    [Fact]
    public void Each_new_break_after_a_repair_is_announced_again()
    {
        var w = new PrinterWatch(Ok);
        Assert.NotNull(w.Observe(Gone));
        Assert.Null(w.Observe(Ok)); // repaired: nothing to warn about
        Assert.Equal(Elsewhere, w.Observe(Elsewhere));
        Assert.Null(w.Observe(Ok));
        Assert.Equal(Gone, w.Observe(Gone));
    }

    [Fact]
    public void A_state_already_announced_at_start_is_not_repeated()
    {
        // The start-up balloon covers the initial state; the watch only reports changes from it.
        var w = new PrinterWatch(Gone);
        Assert.Null(w.Observe(Gone));
        var w2 = new PrinterWatch(Elsewhere);
        Assert.Null(w2.Observe(Elsewhere));
    }

    [Fact]
    public void A_move_to_another_wrong_port_or_from_missing_to_misconfigured_is_a_change()
    {
        var w = new PrinterWatch(Elsewhere);
        var other = new PrinterStatus(PrinterState.Misconfigured, @"C:\x\print.pdf");
        Assert.Equal(other, w.Observe(other));
        Assert.Null(w.Observe(other));
        var w2 = new PrinterWatch(Gone);
        Assert.Equal(Elsewhere, w2.Observe(Elsewhere));
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

/// <summary>The elevated relaunch is one command-line string: a quote in a value would end the argument early.</summary>
public class ElevatedArgumentTests
{
    [Theory]
    [InlineData(@"C:\spool", "LAN \"Fax")]
    [InlineData(@"C:\sp""ool", "LAN Fax")]
    public void A_quote_in_the_printer_name_or_spool_dir_is_refused(string spool, string printer) =>
        Assert.Throws<ArgumentException>(() => PrinterSetup.BuildArguments(@"C:\s.ps1", spool, printer, @"C:\out.log"));

    [Fact]
    public async Task Install_reports_a_refused_value_instead_of_running()
    {
        var script = Path.GetTempFileName();
        try
        {
            var ps = new PrinterSetup(new NoPrinter(), (_, _) => throw new InvalidOperationException("must not run"));
            Assert.Contains("\"", (await ps.InstallAsync(@"C:\spool", "LAN \"Fax", script)).Error);
        }
        finally { File.Delete(script); }
    }

    sealed class NoPrinter : IPrinterKeyReader
    {
        public bool Exists(string printerName) => false;
        public string? Port(string printerName) => null;
    }
}
