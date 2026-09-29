using LANFax.App.Logic;

namespace LANFax.App;

/// <summary>Number entry with live normalization preview and red reason text; raises Changed when validity may have changed.</summary>
internal sealed class NumberBox : TableLayoutPanel
{
    private readonly TextBox _input = new() { Dock = DockStyle.Fill };
    private readonly Label _preview = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Text = " " };
    private readonly Label _error = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(360, 0), Text = "" };

    public event Action? Changed;
    public NumberState State { get; private set; } = NumberInput.Evaluate("");
    public string RawText => _input.Text;

    public NumberBox()
    {
        ColumnCount = 1;
        AutoSize = true;
        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
        Controls.Add(_input);
        Controls.Add(_preview);
        Controls.Add(_error);
        _input.TextChanged += (_, _) => Refresh_();
    }

    public new bool Focus() => _input.Focus();
    public TextBox Input => _input;

    private void Refresh_()
    {
        State = NumberInput.Evaluate(_input.Text);
        _preview.Text = State.Normalized.Length > 0 ? "Will dial: " + State.Normalized : " ";
        _error.Text = State.Reason;
        Changed?.Invoke();
    }
}
