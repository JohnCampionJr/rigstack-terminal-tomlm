using XTerm.Options;

namespace XTerm.Tests;

/// <summary>DECCRA, DECFRA, DECERA -- the rectangle operations, and their shared coordinate rules.</summary>
public class RectangleOpsTests
{
    private const string Esc = "";

    private static Terminal NewTerminal(int cols = 10, int rows = 6) =>
        new(new TerminalOptions { Cols = cols, Rows = rows });

    private static string Row(Terminal t, int row, int count)
    {
        var line = t.Buffer.Lines[row]!;
        return string.Concat(Enumerable.Range(0, count)
            .Select(i => string.IsNullOrEmpty(line[i].Content) ? " " : line[i].Content));
    }

    [Fact]
    public void Fill_covers_the_inclusive_rectangle_and_nothing_else()
    {
        var terminal = NewTerminal();
        terminal.Write($"{Esc}[42;2;2;3;4$x");   // '*' from (2,2) to (3,4)

        Assert.Equal("          ", Row(terminal, 0, 10));
        Assert.Equal(" ***      ", Row(terminal, 1, 10));
        Assert.Equal(" ***      ", Row(terminal, 2, 10));
        Assert.Equal("          ", Row(terminal, 3, 10));
    }

    [Fact]
    public void A_rectangle_is_addressed_in_the_origin_modes_coordinates()
    {
        var terminal = NewTerminal();
        terminal.Write($"{Esc}[2;5r{Esc}[?6h");
        terminal.Write($"{Esc}[42;1;1;1;2$x");   // region-relative (1,1)..(1,2)

        Assert.Equal("**        ", Row(terminal, 1, 10));   // absolute row 2
    }

    [Fact]
    public void A_rectangle_ignores_the_margins_it_crosses()
    {
        // "Ignores margins" is the standard's phrase: the fill is clipped to the SCREEN only.
        var terminal = NewTerminal();
        terminal.Write($"{Esc}[?69h{Esc}[3;5s");
        terminal.Write($"{Esc}[42;1;1;1;8$x");

        Assert.Equal("********  ", Row(terminal, 0, 10));
    }

    [Fact]
    public void An_inverted_rectangle_refuses_the_whole_operation()
    {
        var terminal = NewTerminal();
        terminal.Write("abc");
        terminal.Write($"{Esc}[42;3;3;2;2$x");

        Assert.Equal("abc", Row(terminal, 0, 3));
    }

    [Fact]
    public void Fill_does_not_move_the_cursor()
    {
        var terminal = NewTerminal();
        terminal.Write($"{Esc}[3;4H{Esc}[42;1;1;2;2$x");

        Assert.Equal(2, terminal.Buffer.Y);
        Assert.Equal(3, terminal.Buffer.X);
    }

    [Fact]
    public void Erase_leaves_blanks_where_the_rectangle_was()
    {
        var terminal = NewTerminal();
        terminal.Write("abcdefgh");
        terminal.Write($"{Esc}[1;3;1;5$z");

        Assert.Equal("ab   fgh", Row(terminal, 0, 8));
    }

    [Fact]
    public void Copy_snapshots_the_source_so_overlap_cannot_smear()
    {
        var terminal = NewTerminal();
        terminal.Write("abcdef");
        // Copy (1,1)..(1,4) one column right: overlapping in the smearing direction.
        terminal.Write($"{Esc}[1;1;1;4;1;1;2;1$v");

        Assert.Equal("aabcdf", Row(terminal, 0, 6));
    }

    [Fact]
    public void Copy_carries_the_attributes_with_the_characters()
    {
        var terminal = NewTerminal();
        terminal.Write($"{Esc}[1mZ{Esc}[0m");
        terminal.Write($"{Esc}[1;1;1;1;1;2;1;1$v");

        Assert.True(terminal.Buffer.Lines[1]![0].Attributes.IsBold());
        Assert.Equal("Z", terminal.Buffer.Lines[1]![0].Content);
    }

    [Fact]
    public void Copy_clips_a_destination_hanging_off_the_screen()
    {
        var terminal = NewTerminal(cols: 6, rows: 3);
        terminal.Write("abcdef");
        terminal.Write($"{Esc}[1;1;1;6;1;1;5;1$v");   // dest starts at column 5: only two fit

        Assert.Equal("abcdab", Row(terminal, 0, 6));
    }

    // ---- DECSERA (CSI Pt;Pl;Pb;Pr $ {) -------------------------------------------------------

    [Fact]
    public void SelectiveErase_blanks_the_rectangle()
    {
        var terminal = NewTerminal(cols: 6, rows: 3);
        terminal.Write("abcdef\r\nghijkl");
        terminal.Write($"{Esc}[1;2;2;5${{");

        Assert.Equal("a    f", Row(terminal, 0, 6));
        Assert.Equal("g    l", Row(terminal, 1, 6));
    }

    [Fact]
    public void SelectiveErase_spares_DecscaProtected_cells_only()
    {
        var terminal = NewTerminal(cols: 6, rows: 2);
        terminal.Write("ab");
        terminal.Write($"{Esc}[1\"q" + "CD" + $"{Esc}[0\"q");   // CD protected by DECSCA
        terminal.Write($"{Esc}Ve{Esc}W");                        // e guarded by ISO SPA/EPA
        terminal.Write($"{Esc}[1;1;1;6${{");

        // DECSCA protection holds; the ISO guard belongs to the other erase family and does not.
        Assert.Equal("  CD  ", Row(terminal, 0, 6));
    }
}
