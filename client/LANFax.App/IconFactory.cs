using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace LANFax.App;

/// <summary>Draws the tray icon at runtime (rounded square with an "F"); no binary assets.</summary>
internal static class IconFactory
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Create(bool connected, int size = 32)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            var fill = connected ? Color.FromArgb(0x1F, 0x6F, 0xEB) : Color.FromArgb(0x8A, 0x8A, 0x8A);
            float r = size * 0.22f;
            var rect = new RectangleF(0.5f, 0.5f, size - 1.5f, size - 1.5f);
            using var path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, 2 * r, 2 * r, 180, 90);
            path.AddArc(rect.Right - 2 * r, rect.Y, 2 * r, 2 * r, 270, 90);
            path.AddArc(rect.Right - 2 * r, rect.Bottom - 2 * r, 2 * r, 2 * r, 0, 90);
            path.AddArc(rect.X, rect.Bottom - 2 * r, 2 * r, 2 * r, 90, 90);
            path.CloseFigure();
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
            using var font = new Font("Segoe UI", size * 0.55f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("F", font, Brushes.White, new RectangleF(0, 0, size, size + 1), fmt);
        }
        var h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }
}
