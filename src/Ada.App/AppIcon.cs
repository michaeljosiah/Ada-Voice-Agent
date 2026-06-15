using System.Drawing.Drawing2D;

namespace Ada.App;

/// <summary>Draws Ada's tray icon at runtime — an evergreen disc with a serif "A" monogram — so the
/// shell needs no bundled .ico asset.</summary>
internal static class AppIcon
{
    public static Icon Create()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var disc = new SolidBrush(Color.FromArgb(0x1F, 0x50, 0x40));
            g.FillEllipse(disc, 1, 1, 30, 30);

            using var font = new Font("Georgia", 19, FontStyle.Bold, GraphicsUnit.Pixel);
            using var ink = new SolidBrush(Color.FromArgb(0xF6, 0xEF, 0xE2));
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("A", font, ink, new RectangleF(0, 0, 32, 33), fmt);
        }

        var hicon = bmp.GetHicon();
        return (Icon)Icon.FromHandle(hicon).Clone();
    }
}
