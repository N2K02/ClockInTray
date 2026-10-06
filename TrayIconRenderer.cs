using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ClockInTray;

internal static class TrayIconRenderer
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    // The icon is rendered at the system tray size so the text stays sharp at any DPI.
    public static Icon Render(string text, Color background)
    {
        var size = SystemInformation.SmallIconSize.Width;
        size = Math.Max(size, 16);

        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(background);

            using var family = new FontFamily("Segoe UI");

            // Digit height is the vertical reference so "--" or "WE" are not blown up like "8:59".
            using var digitPath = CreateGlyphPath("0", family);
            var digit = digitPath.GetBounds();

            // Glyphs are packed without their side bearings, and ':' is drawn as two pixel dots,
            // so the digits get as much of the tiny icon width as possible.
            var gap = digit.Height * 0.08f;
            var dot = MathF.Max(2, MathF.Round(size * 0.11f));
            var colonWidth = dot + 2;
            var glyphs = new List<(GraphicsPath? Path, RectangleF Bounds)>();
            try
            {
                var units = 0f;
                var colonPixels = 0f;
                for (var i = 0; i < text.Length; i++)
                {
                    if (text[i] == ':')
                    {
                        glyphs.Add((null, RectangleF.Empty));
                        colonPixels += colonWidth;
                        continue;
                    }

                    var glyph = CreateGlyphPath(text[i].ToString(), family);
                    var glyphBounds = glyph.GetBounds();
                    glyphs.Add((glyph, glyphBounds));
                    units += glyphBounds.Width + (i + 1 < text.Length && text[i + 1] != ':' ? gap : 0);
                }

                // A tray icon is a tiny square, so a normal font would be far smaller than the taskbar clock.
                // Fit the text to the width (minus side padding) and stretch it vertically (within limits) to fill the icon height.
                var padding = MathF.Max(1, MathF.Round(size * 0.08f));
                var scaleX = Math.Min((size - 2 * padding - colonPixels) / units, size / digit.Height);
                var scaleY = Math.Min(size / digit.Height, scaleX * 1.5f);
                var height = digit.Height * scaleY;
                var top = (size - height) / 2f;
                var x = (size - (units * scaleX + colonPixels)) / 2f;

                for (var i = 0; i < glyphs.Count; i++)
                {
                    var (glyph, glyphBounds) = glyphs[i];
                    if (glyph is null)
                    {
                        var dotX = MathF.Round(x + 1);
                        g.FillRectangle(Brushes.White, dotX, MathF.Round(top + height * 0.28f - dot / 2f), dot, dot);
                        g.FillRectangle(Brushes.White, dotX, MathF.Round(top + height * 0.78f - dot / 2f), dot, dot);
                        x += colonWidth;
                        continue;
                    }

                    using var matrix = new Matrix();
                    matrix.Translate(x, top);
                    matrix.Scale(scaleX, scaleY);
                    matrix.Translate(-glyphBounds.X, -digit.Y);
                    glyph.Transform(matrix);
                    g.FillPath(Brushes.White, glyph);
                    x += (glyphBounds.Width + (i + 1 < text.Length && text[i + 1] != ':' ? gap : 0)) * scaleX;
                }
            }
            finally
            {
                foreach (var (glyph, _) in glyphs)
                {
                    glyph?.Dispose();
                }
            }
        }

        // Clone so the native handle can be released immediately.
        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath CreateGlyphPath(string text, FontFamily family)
    {
        var path = new GraphicsPath();
        path.AddString(text, family, (int)FontStyle.Bold, 100f, PointF.Empty, StringFormat.GenericTypographic);
        return path;
    }
}
