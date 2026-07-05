namespace Termiot.Terminal;

// Colors are 0xAARRGGBB, which matches the Bgra32 int pixel layout so cells blit without conversion. The 16-color table is Windows Terminal's Campbell scheme.
public static class Palette
{
    public const uint DefaultFg = 0xFFCCCCCC;
    public const uint DefaultBg = 0xFF000000;
    public const uint EscapeFg = 0xFFFFB86C;
    public const uint SearchBg = 0xFF5C5C1A;
    public const uint SearchCurrentBg = 0xFFB58900;
    public const uint SelectionBg = 0xFF264F78;

    private static readonly uint[] Base16 =
    {
        0xFF0C0C0C, 0xFFC50F1F, 0xFF13A10E, 0xFFC19C00, 0xFF0037DA, 0xFF881798, 0xFF3A96DD, 0xFFCCCCCC,
        0xFF767676, 0xFFE74856, 0xFF16C60C, 0xFFF9F1A5, 0xFF3B78FF, 0xFFB4009E, 0xFF61D6D6, 0xFFF2F2F2,
    };

    public static uint Color16(int index) => Base16[index & 15];

    public static uint Color256(int index)
    {
        index &= 255;
        if (index < 16)
        {
            return Base16[index];
        }
        if (index < 232)
        {
            int v = index - 16;
            int r = CubeLevel(v / 36);
            int g = CubeLevel(v / 6 % 6);
            int b = CubeLevel(v % 6);
            return Rgb(r, g, b);
        }
        int gray = 8 + 10 * (index - 232);
        return Rgb(gray, gray, gray);
    }

    public static uint Rgb(int r, int g, int b) => 0xFF000000 | ((uint)(r & 255) << 16) | ((uint)(g & 255) << 8) | (uint)(b & 255);

    public static uint Brighten(uint color)
    {
        for (int i = 0; i < 8; i++)
        {
            if (Base16[i] == color)
            {
                return Base16[i + 8];
            }
        }
        return color;
    }

    private static int CubeLevel(int v) => v == 0 ? 0 : 55 + v * 40;
}
