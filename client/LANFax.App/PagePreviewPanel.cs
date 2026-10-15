using LANFax.App.Logic;

namespace LANFax.App;

/// <summary>A decoded page image and the stream GDI+ needs to keep open for its lifetime.</summary>
internal sealed class FaxPageImage : IDisposable
{
    private readonly MemoryStream _stream;

    public FaxPageImage(byte[] png)
    {
        _stream = new MemoryStream(png);
        Image = Image.FromStream(_stream);
    }

    public Image Image { get; }

    public void Dispose()
    {
        Image.Dispose();
        _stream.Dispose();
    }
}

/// <summary>
/// The send window's page panel: the server-rendered pages, shown the way they will be
/// faxed (header and page numbers included when the server stamps). One page fills the
/// panel; several tile in as many columns as the width allows, wrapping with a scroll
/// bar. A caption line says what is shown; a centered note covers the empty states
/// (rendering, no documents, unavailable). Owns its images; UI thread only.
/// </summary>
internal sealed class PagePreviewPanel : Panel
{
    private const int Pad = 10;
    private const int MinColumnWidth = 250;   // a page never shrinks below this when tiling
    private const int MaxPageWidth = 520;     // nor grows beyond this when tiles are few

    private readonly Label _caption = new()
    {
        Dock = DockStyle.Top, AutoSize = false, Height = 22, TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = SystemColors.GrayText, Text = " ", Visible = false, Padding = new Padding(2, 4, 2, 0),
    };
    private readonly FlowLayoutPanel _pages = new()
    {
        Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.LeftToRight,
        WrapContents = true, Padding = new Padding(Pad), BackColor = SystemColors.Window,
    };
    private readonly Label _note = new()
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = SystemColors.GrayText,
        Text = " ", Visible = false,
    };
    private readonly List<(PictureBox Box, FaxPageImage Page)> _shown = [];

    public PagePreviewPanel()
    {
        DoubleBuffered = true;
        Controls.Add(_pages);
        Controls.Add(_caption);
        Controls.Add(_note);
        _pages.Visible = false;   // notes paint over a Fill-docked empty panel otherwise
        Resize += (_, _) => FitPages();
    }

    /// <summary>Takes ownership of <paramref name="pages"/> (the panel disposes them later).</summary>
    public void ShowPages(IReadOnlyList<FaxPageImage> pages, string caption)
    {
        Clear();
        _shown.AddRange(pages.Select(p => (new PictureBox
        {
            Image = p.Image, SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(6), Visible = false,
            BackColor = SystemColors.Window,   // the panel's gray shows around each white page: no frame needed
        }, p)));
        foreach (var (box, _) in _shown) _pages.Controls.Add(box);
        _caption.Text = caption;
        _caption.Visible = true;
        _note.Visible = false;
        _pages.Visible = true;
        FitPages();
        foreach (var (box, _) in _shown) box.Visible = true;
    }

    /// <summary>No pages: a centered explanation instead (a load, an empty list, an older server).</summary>
    public void ShowNote(string text)
    {
        Clear();
        _pages.Visible = false;
        _caption.Visible = false;
        _note.Text = text;
        _note.Visible = true;
    }

    public void ShowRendering() => ShowNote("Rendering the pages...");

    protected override void Dispose(bool disposing)
    {
        if (disposing) Clear();
        base.Dispose(disposing);
    }

    private void Clear()
    {
        _pages.Controls.Clear();
        foreach (var (_, page) in _shown) page.Dispose();
        _shown.Clear();
    }

    /// <summary>
    /// One page fills the panel; several tile in as many columns as fit, scaled down to
    /// the wrapped grid's height so a 2–3-page fax fits without scrolling when it can.
    /// </summary>
    private void FitPages()
    {
        if (_shown.Count == 0 || !_pages.Visible) return;
        // the flow panel's own padding and the boxes' margins all come off the budget,
        // or the grid lands a few pixels over and the view scrolls
        int innerW = Math.Max(MinColumnWidth, _pages.ClientSize.Width - _pages.Padding.Horizontal
                              - SystemInformation.VerticalScrollBarWidth);
        int innerH = Math.Max(160, _pages.ClientSize.Height - _pages.Padding.Vertical - 2);
        if (_shown.Count == 1)
        {
            SetSize(_shown[0].Box, innerW - 2, innerH - 2);
            return;
        }
        int columns = Math.Clamp(innerW / MinColumnWidth, 1, _shown.Count);
        int rows = (_shown.Count + columns - 1) / columns;
        // the widest tile that still lets every row fit the panel's height (12 px: box margins)
        int heightBudget = Math.Max(160, (innerH - rows * 12) / rows);
        int thumbW = Math.Min((innerW - columns * 12) / columns, MaxPageWidth);
        foreach (var (box, page) in _shown)
        {
            int w = Math.Min(thumbW, heightBudget * page.Image.Width / Math.Max(1, page.Image.Height));
            SetSize(box, w, Math.Min(w * page.Image.Height / Math.Max(1, page.Image.Width), innerH - 2));
        }
    }

    private static void SetSize(PictureBox box, int w, int h) =>
        box.Size = new Size(Math.Max(80, w), Math.Max(100, h));
}
