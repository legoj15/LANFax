using LANFax.App.Logic;
using LANFax.Core;

namespace LANFax.App;

internal sealed class SettingsDialog : Form
{
    private readonly ClientConfig _cfg;
    private readonly ConfigLocation _loc;
    private readonly TextBox _host = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Dock = DockStyle.Left, Width = 90 };
    private readonly TextBox _token = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly CheckBox _keep = new() { Text = "Keep local copies of sent faxes", AutoSize = true };
    private readonly Button _test = new() { Text = "Test connection", AutoSize = true };
    private readonly Label _result = new() { AutoSize = true, MaximumSize = new Size(420, 0) };
    private readonly Button _clearToken = new() { Text = "Clear token", AutoSize = true };
    private readonly Label _tokenHint = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Icon _icon = IconFactory.Create(true);
    private readonly Func<bool> _sendInFlight;
    private bool _tokenCleared;

    public const string WaitForSendText = "Wait for the current fax to finish before saving settings.";
    public const string TokenRuleText = ClientConfig.TokenRule + " (paste it exactly as the server printed it).";

    /// <param name="sendInFlight">True while a fax is on the wire: saving then could move its retries to another server.</param>
    public SettingsDialog(ClientConfig cfg, ConfigLocation loc, Func<bool> sendInFlight)
    {
        _cfg = cfg; _loc = loc; _sendInFlight = sendInFlight;
        Text = "LANFax settings";
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = _icon;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        _host.Text = cfg.Server;
        _port.Value = Math.Clamp(cfg.Port, 1, 65535);
        _keep.Checked = cfg.KeepLocalCopies;

        var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        void Row(string label, Control c, int r)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) }, 0, r);
            t.Controls.Add(c, 1, r);
        }
        Row("Server (host or IP):", _host, 0);
        Row("Port:", _port, 1);
        Row("Token:", _token, 2);
        _tokenHint.Text = string.IsNullOrEmpty(cfg.Token) ? "No token stored yet." : "Leave blank to keep the stored token.";
        _clearToken.Enabled = !string.IsNullOrEmpty(cfg.Token);
        var tokenRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        tokenRow.Controls.Add(_tokenHint);
        tokenRow.Controls.Add(_clearToken);
        t.Controls.Add(tokenRow, 1, 3);
        t.Controls.Add(_keep, 0, 4); t.SetColumnSpan(_keep, 2);
        t.Controls.Add(_test, 0, 5); t.SetColumnSpan(_test, 2);
        t.Controls.Add(_result, 0, 6); t.SetColumnSpan(_result, 2);
        var where = new Label
        {
            AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(420, 0),
            Text = "Settings file: " + loc.Path + (loc.IsFallback
                ? "\r\n(ProgramData\\LANFax is not writable for your account, so settings are stored in your profile instead.)"
                : ""),
        };
        t.Controls.Add(where, 0, 7); t.SetColumnSpan(where, 2);

        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(90, 28) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 28) };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
        t.Controls.Add(buttons, 0, 8); t.SetColumnSpan(buttons, 2);
        Controls.Add(t);
        AcceptButton = ok; CancelButton = cancel;

        _test.Click += async (_, _) => await TestAsync();
        _clearToken.Click += (_, _) =>
        {
            _tokenCleared = true;
            _token.Text = "";
            _clearToken.Enabled = false;
            _tokenHint.Text = "The stored token will be removed when you save.";
        };
        _token.TextChanged += (_, _) => { if (_token.Text.Length > 0) _tokenCleared = false; };
        FormClosing += (_, e) =>
        {
            if (DialogResult == DialogResult.OK && !Save()) e.Cancel = true;
        };
    }

    private ClientConfig Candidate() => new()
    {
        Server = _host.Text.Trim(),
        Port = (int)_port.Value,
        Token = _token.Text.Trim().Length > 0 ? _token.Text.Trim() : _tokenCleared ? null : _cfg.Token,
        PrinterName = _cfg.PrinterName,
        SpoolDir = _cfg.SpoolDir,
        KeepLocalCopies = _keep.Checked,
    };

    private async Task TestAsync()
    {
        if (string.IsNullOrWhiteSpace(_host.Text)) { _result.ForeColor = Color.Firebrick; _result.Text = "Enter a server first."; return; }
        _test.Enabled = false;
        _result.ForeColor = SystemColors.GrayText;
        _result.Text = "Testing...";
        try
        {
            var cand = Candidate();
            if (cand.Validate() is { } invalid) { _result.ForeColor = Color.Firebrick; _result.Text = "Cannot use these settings: " + invalid + "."; return; }
            if (ClientConfig.TokenError(cand.Token) != null) { _result.ForeColor = Color.Firebrick; _result.Text = TokenRuleText; return; }
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var st = await new FaxApi(cand, http).GetStatusAsync();
            var regs = st.Registrations.Count == 0
                ? "none"
                : string.Join(", ", st.Registrations.Select(r => $"{r.Name} ({r.Status})"));
            var arm = st.Arm.Armed ? $"armed, {st.Arm.RemainingMin} min left" : "not armed";
            _result.ForeColor = Color.ForestGreen;
            _result.Text = $"Connected. Registrations: {regs}. Receiving: {arm}.";
        }
        catch (FaxApiException e)
        {
            _result.ForeColor = Color.Firebrick;
            _result.Text = e.Message;
        }
        catch (Exception e)
        {
            _result.ForeColor = Color.Firebrick;
            _result.Text = e.Message;
        }
        finally { _test.Enabled = true; }
    }

    private bool Save()
    {
        if (_sendInFlight())
        {
            MessageBox.Show(this, WaitForSendText, "LANFax", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        if (string.IsNullOrWhiteSpace(_host.Text))
        {
            MessageBox.Show(this, "Enter a server host or IP.", "LANFax", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        var c = Candidate();
        if (c.Validate() is { } invalid)
        {
            MessageBox.Show(this, "These settings cannot be used: " + invalid + ".", "LANFax", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        if (ClientConfig.TokenError(c.Token) != null)
        {
            MessageBox.Show(this, TokenRuleText, "LANFax", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        try { c.Save(_loc.Path, ClientConfig.DefaultTokenPath); }
        catch (Exception e)
        {
            FileLog.Error("saving settings failed", e);
            MessageBox.Show(this, "Could not save settings: " + e.Message, "LANFax", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        _cfg.Server = c.Server; _cfg.Port = c.Port; _cfg.Token = c.Token; _cfg.KeepLocalCopies = c.KeepLocalCopies;
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _icon.Dispose();
        base.Dispose(disposing);
    }
}
