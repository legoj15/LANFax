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

    [Fact]
    public void Checkbox_off_routes_document()
    {
        var p = SendPlan.Create(G, @"C:\spool\jobs\x.pdf", "555 234 5678", "hi", false, false);
        Assert.Equal(SendRoute.Document, p.Route);
        Assert.Equal("15552345678", p.Number);
        Assert.Equal("hi", p.Subject);
        Assert.Equal(G.ToString(), p.IdempotencyKey);
    }

    [Fact]
    public void Checkbox_on_routes_test_page_and_discards_pdf_regardless_of_keep()
    {
        var p = SendPlan.Create(G, @"C:\spool\jobs\x.pdf", "5552345678", null, true, true);
        Assert.Equal(SendRoute.TestPage, p.Route);
        Assert.Equal(FileAction.Delete, p.AfterAccept);
    }

    [Fact]
    public void Keep_moves_to_sent_else_deletes()
    {
        var keep = SendPlan.Create(G, @"C:\spool\jobs\x.pdf", "5552345678", null, false, true);
        Assert.Equal(FileAction.MoveToSent, keep.AfterAccept);
        Assert.Equal(Path.Combine(@"C:\spool", "sent", G + ".pdf"), keep.SentPath(@"C:\spool"));
        var del = SendPlan.Create(G, @"C:\spool\jobs\x.pdf", "5552345678", null, false, false);
        Assert.Equal(FileAction.Delete, del.AfterAccept);
    }

    [Fact]
    public void Blank_subject_becomes_null()
    {
        Assert.Null(SendPlan.Create(G, "p", "5552345678", "  ", false, false).Subject);
    }

    [Fact]
    public void Invalid_number_throws()
    {
        Assert.Throws<ArgumentException>(() => SendPlan.Create(G, "p", "911", null, false, false));
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
        Assert.Equal("Fax to 15552345678 queued", NotificationText.Queued("15552345678"));
        var until = new DateTimeOffset(2026, 9, 29, 14, 5, 0, TimeSpan.Zero);
        Assert.Contains(until.ToLocalTime().ToString("t"), NotificationText.ReceiveArmed(until));
    }
}

public class PrinterSetupTests
{
    sealed class FakeReader(params string[] keys) : IPrinterKeyReader
    {
        public bool Exists(string printerName) => keys.Contains(printerName, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Detects_present_and_absent()
    {
        var ps = new PrinterSetup(new FakeReader("LAN Fax"));
        Assert.True(ps.IsInstalled("LAN Fax"));
        Assert.True(ps.IsInstalled("lan fax"));
        Assert.False(ps.IsInstalled("Other"));
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
}
