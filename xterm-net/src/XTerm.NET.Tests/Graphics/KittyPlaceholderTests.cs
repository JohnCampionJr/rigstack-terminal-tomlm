using XTerm.Options;

namespace XTerm.Tests.Graphics;

/// <summary>
/// Unicode placeholders: U+10EEEE cells that say "part of a picture belongs here", with the image
/// named by the cell's foreground colour rather than by an escape sequence.
///
/// <para>It is how image.nvim, yazi and ranger place pictures, and it works because the colour is
/// carried as data: <c>AttributeData</c> keeps 25 bits for a foreground value, so a 24-bit id
/// survives the round trip.</para>
///
/// <para>Which tile a cell shows is worked out from where it sits relative to the top-left of the
/// run. The protocol also allows row and column to be stated with combining marks from a fixed
/// table of 297 characters; that is not implemented, and those marks are ignored — see
/// <see cref="Diacritics_are_ignored_rather_than_misread"/>.</para>
/// </summary>
public class KittyPlaceholderTests
{
    private const string Esc = "\u001b";
    private const string St = Esc + "\\";
    private const string Placeholder = "\U0010EEEE";

    private static Terminal Fresh()
        => new(new TerminalOptions
        {
            Cols = 30,
            Rows = 12,
            CellWidthPixels = 2,
            CellHeightPixels = 3
        });

    private static string SolidRgba(int width, int height, byte value)
    {
        var bytes = new byte[width * height * 4];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = value;
        for (int i = 3; i < bytes.Length; i += 4)
            bytes[i] = 255;
        return Convert.ToBase64String(bytes);
    }

    /// <summary>Transmits a 4x6 picture under an id, showing nothing.</summary>
    private static Terminal WithStoredImage(uint id, byte value = 90)
    {
        var terminal = Fresh();
        terminal.Write($"{Esc}_Ga=t,i={id},f=32,s=4,v=6,q=2;{SolidRgba(4, 6, value)}{St}");
        return terminal;
    }

    /// <summary>Sets the foreground to a 24-bit value, which is where the id travels.</summary>
    private static string SelectImageId(uint id)
        => $"{Esc}[38;2;{(id >> 16) & 0xFF};{(id >> 8) & 0xFF};{id & 0xFF}m";

    private static XTerm.Buffer.BufferCell Cell(Terminal terminal, int col, int row)
        => terminal.Buffer.Lines[terminal.Buffer.YBase + row]![col];

    [Fact]
    public void A_placeholder_cell_shows_the_image_its_colour_names()
    {
        var terminal = WithStoredImage(5);

        terminal.Write(SelectImageId(5) + Placeholder);

        var cell = Cell(terminal, 0, 0);
        Assert.NotNull(cell.Placement);
        Assert.Equal(0, cell.ImageCol);
        Assert.Equal(0, cell.ImageRow);
    }

    /// <summary>
    /// A run of them is a rectangle, and each cell works out its tile from where it sits. Written in
    /// reading order, which is how every client that uses placeholders emits them.
    /// </summary>
    [Fact]
    public void A_run_of_placeholders_maps_to_consecutive_tiles()
    {
        var terminal = WithStoredImage(5);

        terminal.Write(SelectImageId(5));
        terminal.Write(Placeholder + Placeholder);
        terminal.Write($"{Esc}[2;1H");
        terminal.Write(Placeholder + Placeholder);

        for (int row = 0; row < 2; row++)
        {
            for (int col = 0; col < 2; col++)
            {
                var cell = Cell(terminal, col, row);
                Assert.True(cell.Placement is not null, $"cell ({col},{row}) holds no picture");
                Assert.Equal(col, cell.ImageCol);
                Assert.Equal(row, cell.ImageRow);
            }
        }
    }

    /// <summary>All the cells of one run show the same picture, so a host uploads it once.</summary>
    [Fact]
    public void Every_cell_of_a_run_shares_one_image()
    {
        var terminal = WithStoredImage(5);

        terminal.Write(SelectImageId(5) + Placeholder + Placeholder);

        Assert.Same(Cell(terminal, 0, 0).Image, Cell(terminal, 1, 0).Image);
    }

    /// <summary>
    /// An id nothing was transmitted under cannot resolve, so the character stays text rather than
    /// silently becoming a blank.
    /// </summary>
    [Fact]
    public void An_unknown_id_prints_as_an_ordinary_character()
    {
        var terminal = WithStoredImage(5);

        terminal.Write(SelectImageId(999) + Placeholder);

        Assert.Null(Cell(terminal, 0, 0).Placement);
        Assert.Equal(Placeholder, Cell(terminal, 0, 0).Content);
    }

    /// <summary>
    /// A palette index is a colour, not an id. Only a direct colour carries one, so an ordinary
    /// coloured placeholder does not accidentally summon image number three.
    /// </summary>
    [Fact]
    public void A_palette_colour_is_not_read_as_an_id()
    {
        var terminal = WithStoredImage(3);

        terminal.Write($"{Esc}[31m" + Placeholder);   // SGR 31, palette red

        Assert.Null(Cell(terminal, 0, 0).Placement);
    }

    /// <summary>
    /// The combining marks that state a row and column explicitly are consumed and ignored rather
    /// than misread. A contiguous rectangle written in order is unaffected, because the position
    /// already says the same thing; a client placing a scattered subset of tiles is not supported.
    /// </summary>
    [Fact]
    public void Diacritics_are_ignored_rather_than_misread()
    {
        var terminal = WithStoredImage(5);

        // U+0305 is the first entry of the protocol's row/column table.
        terminal.Write(SelectImageId(5) + Placeholder + "\u0305");

        var cell = Cell(terminal, 0, 0);
        Assert.NotNull(cell.Placement);
        Assert.Equal(0, cell.ImageRow);
        Assert.Equal(" ", cell.Content);
    }

    [Fact]
    public void Placeholders_do_nothing_when_kitty_is_switched_off()
    {
        var terminal = new Terminal(new TerminalOptions
        {
            Cols = 30,
            Rows = 12,
            CellWidthPixels = 2,
            CellHeightPixels = 3,
            KittyGraphicsEnabled = false
        });

        terminal.Write(SelectImageId(5) + Placeholder);

        Assert.Null(Cell(terminal, 0, 0).Placement);
    }

    /// <summary>Placeholder cells are cells, so everything that happens to text happens to them.</summary>
    [Fact]
    public void A_placeholder_cell_behaves_like_terminal_content()
    {
        var terminal = WithStoredImage(5);
        terminal.Write(SelectImageId(5) + Placeholder + Placeholder);

        terminal.Write($"{Esc}[1;1H{Esc}[39mX");

        Assert.Null(Cell(terminal, 0, 0).Placement);
        Assert.Equal("X", Cell(terminal, 0, 0).Content);
        Assert.NotNull(Cell(terminal, 1, 0).Placement);
    }
}
