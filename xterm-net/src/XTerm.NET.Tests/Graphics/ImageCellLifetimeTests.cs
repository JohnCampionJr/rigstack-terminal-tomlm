using XTerm.Buffer;
using XTerm.Graphics;
using XTerm.Options;

namespace XTerm.Tests.Graphics;

/// <summary>
/// The behaviour that justifies keeping images on the cells rather than in an overlay: a picture
/// is terminal content, and everything that happens to text has to happen to it.
///
/// <para>Printing over a cell replaces it, erasing clears it, scrolling carries it, and falling
/// off the end of the scrollback disposes of it -- none of which needed code, because a cell is a
/// struct and every one of those paths already builds or copies whole cells. These tests exist to
/// keep it that way.</para>
/// </summary>
public class ImageCellLifetimeTests
{
    private const string Esc = "\u001b";
    private const string St = Esc + "\\";

    /// <summary>Four pixels wide, twelve tall: two cells across, four down.</summary>
    private const string TwoByFourCells = "#0;2;100;0;0!4~-!4~";

    private static Terminal Fresh(Action<TerminalOptions>? configure = null)
    {
        var options = new TerminalOptions
        {
            Cols = 20,
            Rows = 10,
            CellWidthPixels = 2,
            CellHeightPixels = 3
        };
        configure?.Invoke(options);
        return new Terminal(options);
    }

    private static void WriteSixel(Terminal terminal, string body = TwoByFourCells)
        => terminal.Write($"{Esc}P0;1;0q{body}{St}");

    private static BufferCell Cell(Terminal terminal, int col, int screenRow)
        => terminal.Buffer.Lines[terminal.Buffer.YBase + screenRow]![col];

    private static int ImageCellCount(Terminal terminal)
    {
        int count = 0;
        for (int i = 0; i < terminal.Buffer.Lines.Length; i++)
        {
            var line = terminal.Buffer.Lines[i];
            if (line is null)
                continue;
            for (int x = 0; x < line.Length; x++)
            {
                if (line[x].Image is not null)
                    count++;
            }
        }
        return count;
    }

    [Fact]
    public void Printing_over_a_tile_replaces_it_with_the_character()
    {
        var terminal = Fresh();
        WriteSixel(terminal);

        terminal.Write($"{Esc}[1;1HX");

        var cell = Cell(terminal, 0, 0);
        Assert.Null(cell.Image);
        Assert.Equal("X", cell.Content);

        // and only that cell
        Assert.NotNull(Cell(terminal, 1, 0).Image);
        Assert.NotNull(Cell(terminal, 0, 1).Image);
    }

    [Fact]
    public void Erase_in_line_clears_the_tiles_on_that_row()
    {
        var terminal = Fresh();
        WriteSixel(terminal);

        terminal.Write($"{Esc}[1;1H{Esc}[K");

        Assert.Null(Cell(terminal, 0, 0).Image);
        Assert.Null(Cell(terminal, 1, 0).Image);
        Assert.NotNull(Cell(terminal, 0, 1).Image);
    }

    [Fact]
    public void Erase_in_display_clears_every_tile_on_screen()
    {
        var terminal = Fresh();
        WriteSixel(terminal);

        terminal.Write($"{Esc}[2J");

        Assert.Equal(0, ImageCellCount(terminal));
    }

    [Fact]
    public void Erase_characters_clears_the_tiles_it_covers()
    {
        var terminal = Fresh();
        WriteSixel(terminal);

        terminal.Write($"{Esc}[1;1H{Esc}[1X");

        Assert.Null(Cell(terminal, 0, 0).Image);
        Assert.NotNull(Cell(terminal, 1, 0).Image);
    }

    [Fact]
    public void A_full_reset_clears_every_tile()
    {
        var terminal = Fresh();
        WriteSixel(terminal);

        terminal.Write($"{Esc}c");

        Assert.Equal(0, ImageCellCount(terminal));
    }

    [Fact]
    public void Scrolling_carries_the_tiles_with_their_lines()
    {
        var terminal = Fresh();
        WriteSixel(terminal);
        var image = Cell(terminal, 0, 0).Image;
        Assert.NotNull(image);

        // The absolute line the top of the image went onto. Scrolling moves the viewport over the
        // buffer, so this index is what should stay put while the screen row changes.
        var topLine = terminal.Buffer.YBase;

        // Push the screen up by two rows from the bottom.
        terminal.Write($"{Esc}[10;1H\r\n\r\n");

        Assert.Equal(2, terminal.Buffer.YBase - topLine);

        // Nothing copied the tiles anywhere: the same line object still holds them, unchanged.
        var moved = terminal.Buffer.Lines[topLine]![0];
        Assert.True(ReferenceEquals(moved.Image, image),
            "the tile did not travel with its line");
        Assert.Equal(0, moved.ImageRow);

        // Which puts the top of the picture two rows higher on screen than it was.
        Assert.True(ReferenceEquals(Cell(terminal, 0, -2).Image, image));
        Assert.Equal(8, ImageCellCount(terminal));
    }

    /// <summary>
    /// The disposal story: an image dies with the last cell holding it, so a picture that scrolls
    /// out of a short scrollback leaves nothing behind to evict.
    /// </summary>
    [Fact]
    public void An_image_scrolled_out_of_the_scrollback_leaves_no_references()
    {
        var terminal = Fresh(o => o.Scrollback = 4);
        WriteSixel(terminal);
        Assert.Equal(8, ImageCellCount(terminal));

        terminal.Write($"{Esc}[10;1H");
        for (int i = 0; i < 40; i++)
            terminal.Write("\r\n");

        Assert.Equal(0, ImageCellCount(terminal));
    }

    /// <summary>
    /// Reflow re-wraps a logical line by copying ranges of cells between lines, so tiles carried
    /// through it would reassemble as a shuffled mosaic -- every piece intact, in the wrong place.
    /// </summary>
    [Fact]
    public void A_change_of_width_drops_the_images()
    {
        var terminal = Fresh();
        WriteSixel(terminal);
        Assert.Equal(8, ImageCellCount(terminal));

        terminal.Resize(15, 10);

        Assert.Equal(0, ImageCellCount(terminal));
    }

    /// <summary>A change of height moves whole lines, so there is nothing to be confused about.</summary>
    [Fact]
    public void A_change_of_height_alone_keeps_the_images()
    {
        var terminal = Fresh();
        WriteSixel(terminal);

        terminal.Resize(20, 14);

        Assert.Equal(8, ImageCellCount(terminal));
    }

    [Fact]
    public void Clearing_a_dropped_image_leaves_a_blank_rather_than_a_hole()
    {
        var terminal = Fresh();
        WriteSixel(terminal);

        terminal.Resize(15, 10);

        var cell = terminal.Buffer.Lines[terminal.Buffer.YBase]![0];
        Assert.Equal(" ", cell.Content);
        Assert.Equal(1, cell.Width);
    }

    /// <summary>
    /// The backstop for the case reference counting cannot reach: a deep scrollback full of
    /// pictures, every one still referenced and every one still in memory.
    /// </summary>
    [Fact]
    public void The_oldest_images_are_dropped_when_the_budget_is_exceeded()
    {
        // Each image here is 4x12 BGRA -- 192 bytes -- so a 200 byte budget holds exactly one.
        var terminal = Fresh(o => o.MaxImageBytes = 200);

        WriteSixel(terminal);
        var first = Cell(terminal, 0, 0).Image;
        Assert.NotNull(first);

        WriteSixel(terminal);
        var second = Cell(terminal, 0, 4).Image;
        Assert.NotNull(second);

        Assert.Null(Cell(terminal, 0, 0).Image);
        Assert.True(ReferenceEquals(Cell(terminal, 0, 4).Image, second),
            "the newest image should be the one that survives");
    }

    [Fact]
    public void A_generous_budget_keeps_both_images()
    {
        var terminal = Fresh(o => o.MaxImageBytes = 64 * 1024);

        WriteSixel(terminal);
        WriteSixel(terminal);

        Assert.NotNull(Cell(terminal, 0, 0).Image);
        Assert.NotNull(Cell(terminal, 0, 4).Image);
    }

    /// <summary>
    /// Two cells showing different pieces of the same picture are not interchangeable, however
    /// alike their text is. Renderers coalesce adjacent cells into one run by comparing them.
    /// </summary>
    [Fact]
    public void Cells_showing_different_tiles_are_not_equal()
    {
        var terminal = Fresh();
        WriteSixel(terminal);

        var left = Cell(terminal, 0, 0);
        var right = Cell(terminal, 1, 0);

        Assert.Equal(left.Content, right.Content);
        Assert.NotEqual(left, right);
    }

    [Fact]
    public void An_image_cell_is_not_equal_to_a_plain_space()
    {
        var terminal = Fresh();
        WriteSixel(terminal);

        Assert.NotEqual(BufferCell.Space, Cell(terminal, 0, 0));
    }

    [Fact]
    public void The_alternate_buffer_keeps_its_own_images()
    {
        var terminal = Fresh();
        WriteSixel(terminal);

        terminal.Write($"{Esc}[?1049h"); // to the alternate screen
        Assert.Equal(0, ImageCellCount(terminal));

        terminal.Write($"{Esc}[?1049l"); // and back
        Assert.Equal(8, ImageCellCount(terminal));
    }
}
