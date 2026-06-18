using System.Drawing.Drawing2D;

namespace Ada.App;

/// <summary>
/// Draws Ada's tray icon at runtime so the shell needs no bundled .ico asset. It's the same "aurora"
/// mark as the in-app logo — a core dot with three arcs radiating to the right, in Ada's teal → purple →
/// coral gradient — translated from the SVG used in the UI header (a 64×64 canvas, centre (22,32), the
/// arcs being concentric circles of radius 14/22/30 each opening ±55° to the east and fading outward).
/// </summary>
internal static class AppIcon
{
    public static Icon Create()
    {
        const int S = 32;
        using var bmp = new Bitmap(S, S);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            const float k = S / 64f;          // the logo is authored on a 64-unit canvas
            float cx = 22 * k, cy = 32 * k;   // shared centre of the dot and all three arcs

            Arc(g, S, cx, cy, 14 * k, 5.0f * k, 255); // inner arc, full strength
            Arc(g, S, cx, cy, 22 * k, 4.2f * k, 158); // ~0.62 opacity
            Arc(g, S, cx, cy, 30 * k, 3.4f * k, 97);  // ~0.38 opacity

            using var dot = Gradient(S, 255);
            float dr = 5.6f * k;
            g.FillEllipse(dot, cx - dr, cy - dr, dr * 2, dr * 2);
        }

        var hicon = bmp.GetHicon();
        return (Icon)Icon.FromHandle(hicon).Clone();
    }

    // One arc of radius r centred at (cx,cy), spanning -55°..+55° (opening east), stroked with the gradient.
    private static void Arc(Graphics g, int size, float cx, float cy, float r, float width, int alpha)
    {
        using var brush = Gradient(size, alpha);
        using var pen = new Pen(brush, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, cx - r, cy - r, r * 2, r * 2, -55f, 110f);
    }

    // Ada's aurora gradient (teal → purple → coral), spanning the whole icon on a ~vertical diagonal so
    // each tall arc traverses the full spectrum top-to-bottom — matching the SVG's per-shape gradient.
    private static LinearGradientBrush Gradient(int size, int alpha)
    {
        var brush = new LinearGradientBrush(new RectangleF(0, 0, size, size), Color.Empty, Color.Empty, 60f);
        brush.InterpolationColors = new ColorBlend
        {
            Colors = new[]
            {
                Color.FromArgb(alpha, 0x0A, 0x7C, 0x84),
                Color.FromArgb(alpha, 0x6F, 0x5C, 0xD0),
                Color.FromArgb(alpha, 0xEB, 0x5C, 0x37),
            },
            Positions = new[] { 0f, 0.55f, 1f },
        };
        return brush;
    }
}
