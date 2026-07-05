using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Termiot.Ui;

// Rasterizes each glyph once (per bold variant) into an alpha map at device-pixel cell size; the terminal control then color-blends these into its backbuffer. FormattedText handles font fallback, so characters Consolas lacks (e.g. the U+24xx control pictures used for literal escape rendering) still get glyphs.
public sealed class GlyphAtlas
{
    public const string FontName = "Consolas";
    public const double FontSizeDips = 15.0;

    public readonly int CellWidth;
    public readonly int CellHeight;

    private readonly double _fontSizePx;
    private readonly Typeface _normal = new(new FontFamily(FontName), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private readonly Typeface _bold = new(new FontFamily(FontName), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private readonly Dictionary<int, byte[]> _glyphs = new();

    public GlyphAtlas(double dpiScale)
    {
        _fontSizePx = FontSizeDips * dpiScale;
        var measure = MakeText("M", false);
        CellWidth = Math.Max(1, (int)Math.Ceiling(measure.WidthIncludingTrailingWhitespace));
        CellHeight = Math.Max(1, (int)Math.Ceiling(measure.Height));
    }

    public byte[] Get(char ch, bool bold)
    {
        int key = ch | (bold ? 1 << 21 : 0);
        if (_glyphs.TryGetValue(key, out var alpha))
        {
            return alpha;
        }
        alpha = Rasterize(ch, bold);
        _glyphs[key] = alpha;
        return alpha;
    }

    private FormattedText MakeText(string s, bool bold)
    {
        return new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? _bold : _normal, _fontSizePx, Brushes.White, 1.0);
    }

    private byte[] Rasterize(char ch, bool bold)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawText(MakeText(ch.ToString(), bold), new Point(0, 0));
        }
        var bmp = new RenderTargetBitmap(CellWidth, CellHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var pixels = new byte[CellWidth * CellHeight * 4];
        bmp.CopyPixels(pixels, CellWidth * 4, 0);
        var alpha = new byte[CellWidth * CellHeight];
        for (int i = 0; i < alpha.Length; i++)
        {
            alpha[i] = pixels[i * 4 + 3];
        }
        return alpha;
    }
}
