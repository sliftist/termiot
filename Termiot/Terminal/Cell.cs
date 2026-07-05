namespace Termiot.Terminal;

[Flags]
public enum CellFlags : byte
{
    None = 0,
    Bold = 1,
    Underline = 2,
    Reverse = 4,
    Escape = 8,
}

public struct Cell
{
    public char Ch;
    public uint Fg;
    public uint Bg;
    public CellFlags Flags;
}

public sealed class LineAnnotation
{
    public int Start;
    public int Length;
    public string Desc = "";
}

public sealed class TermLine
{
    public Cell[] Cells;
    public List<LineAnnotation>? Annotations;

    public TermLine(int cols, Cell blank)
    {
        Cells = new Cell[cols];
        Array.Fill(Cells, blank);
    }

    public void Resize(int cols, Cell blank)
    {
        if (cols == Cells.Length)
        {
            return;
        }
        int old = Cells.Length;
        Array.Resize(ref Cells, cols);
        for (int i = old; i < cols; i++)
        {
            Cells[i] = blank;
        }
    }

    // Scrollback lines drop trailing blank cells so a large history doesn't hold full-width arrays for mostly-empty lines.
    public void TrimTrailingBlanks()
    {
        int end = Cells.Length;
        while (end > 0 && Cells[end - 1].Ch == ' ' && Cells[end - 1].Bg == Palette.DefaultBg && Cells[end - 1].Flags == CellFlags.None)
        {
            end--;
        }
        if (end < Cells.Length)
        {
            Array.Resize(ref Cells, end);
        }
    }

    public string GetText()
    {
        var chars = new char[Cells.Length];
        for (int i = 0; i < Cells.Length; i++)
        {
            chars[i] = Cells[i].Ch;
        }
        return new string(chars).TrimEnd();
    }
}
