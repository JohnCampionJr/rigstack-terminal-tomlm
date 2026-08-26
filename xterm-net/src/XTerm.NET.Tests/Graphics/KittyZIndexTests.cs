using XTerm.Options;

namespace XTerm.Tests.Graphics;

/// <summary>
/// Draw order, from Kitty's <c>z=</c> key.
///
/// <para>A cell holds one placement, so ordering between two pictures is expressed by which of them
/// the cell keeps rather than by blending them. That is exact for opaque pictures and loses only the
/// blend where a translucent one overlaps another -- see
/// <see cref="Two_overlapping_pictures_are_not_blended"/>, which records the limit rather than
/// pretending it is not there.</para>
///
/// <para>Negative z means something different in kind: behind the TEXT. There the cell keeps both,
/// because a background image that vanished the moment anything was typed on it would be useless.
/// </para>
/// </summary>
public class KittyZIndexTests
{
    private const string Esc = "\u001b";
    private const string St = Esc + "\\";

    private static Terminal Fresh()
        => new(new TerminalOptions
        {
            Cols = 30,
            Rows = 12,
            CellWidthPixels = 2,
            CellHeightPixels = 3
        });

    private static string Apc(string control, string payload = "")
        => payload.Length == 0 ? $"{Esc}_G{control}{St}" : $"{Esc}_G{control};{payload}{St}";

    /// <summary>A 4x6 picture, which covers two cells by two at the metrics above.</summary>
    private static string Pixels() => Convert.ToBase64String(new byte[4 * 6 * 4]);

    private static XTerm.Buffer.BufferCell Cell(Terminal terminal, int col, int row)
        => terminal.Buffer.Lines[terminal.Buffer.YBase + row]![col];

    /// <summary>Transmits two pictures under ids 1 and 2 so they can be told apart by reference.</summary>
    private static Terminal WithTwoImages()
    {
        var terminal = Fresh();
        terminal.Write(Apc("a=t,i=1,f=32,s=4,v=6,q=2", Pixels()));
        terminal.Write(Apc("a=t,i=2,f=32,s=4,v=6,q=2", Pixels()));
        return terminal;
    }

    private static void PlaceAt(Terminal terminal, uint id, int col, int row, int z)
    {
        terminal.Write($"{Esc}[{row + 1};{col + 1}H");
        terminal.Write(Apc($"a=p,i={id},z={z},C=1,q=2"));
    }

    // ---- ordering between pictures ----------------------------------------------------------------

    [Fact]
    public void A_higher_z_picture_is_not_displaced_by_a_lower_one()
    {
        var terminal = WithTwoImages();
        PlaceAt(terminal, 1, 0, 0, z: 5);

        PlaceAt(terminal, 2, 0, 0, z: 1);

        var placement = Cell(terminal, 0, 0).Placement;
        Assert.NotNull(placement);
        Assert.Equal(5, placement!.ZIndex);
    }

    [Fact]
    public void A_higher_z_picture_displaces_a_lower_one()
    {
        var terminal = WithTwoImages();
        PlaceAt(terminal, 1, 0, 0, z: 1);

        PlaceAt(terminal, 2, 0, 0, z: 5);

        var placement = Cell(terminal, 0, 0).Placement;
        Assert.NotNull(placement);
        Assert.Equal(5, placement!.ZIndex);
    }

    /// <summary>At the same depth the newer picture wins, which is the order they were drawn in.</summary>
    [Fact]
    public void At_equal_z_the_newer_picture_wins()
    {
        var terminal = WithTwoImages();
        PlaceAt(terminal, 1, 0, 0, z: 3);
        var first = Cell(terminal, 0, 0).Placement;

        PlaceAt(terminal, 2, 0, 0, z: 3);

        Assert.NotSame(first, Cell(terminal, 0, 0).Placement);
    }

    /// <summary>
    /// Only the overlapping cells are affected. A lower picture keeps the part of itself that the
    /// higher one does not cover, rather than being dropped whole.
    /// </summary>
    [Fact]
    public void A_partly_covered_picture_keeps_the_part_that_shows()
    {
        var terminal = WithTwoImages();
        PlaceAt(terminal, 1, 0, 0, z: 1);          // columns 0-1
        var lower = Cell(terminal, 0, 0).Placement;

        PlaceAt(terminal, 2, 1, 0, z: 5);          // columns 1-2

        Assert.Same(lower, Cell(terminal, 0, 0).Placement);
        Assert.NotSame(lower, Cell(terminal, 1, 0).Placement);
    }

    /// <summary>
    /// The limit of one placement per cell, recorded deliberately. Where two pictures overlap the
    /// front one is shown outright rather than composited over the one behind, so a translucent
    /// picture does not let the lower one through.
    /// </summary>
    [Fact]
    public void Two_overlapping_pictures_are_not_blended()
    {
        var terminal = WithTwoImages();
        PlaceAt(terminal, 1, 0, 0, z: 1);
        PlaceAt(terminal, 2, 0, 0, z: 5);

        var cell = Cell(terminal, 0, 0);
        Assert.NotNull(cell.Placement);
        Assert.Equal(5, cell.Placement!.ZIndex);
    }

    // ---- behind the text --------------------------------------------------------------------------

    /// <summary>A picture at negative z goes under text already on the screen and leaves it there.</summary>
    [Fact]
    public void A_negative_z_picture_keeps_the_text_it_covers()
    {
        var terminal = WithTwoImages();
        terminal.Write($"{Esc}[1;1HAB");

        PlaceAt(terminal, 1, 0, 0, z: -1);

        Assert.Equal("A", Cell(terminal, 0, 0).Content);
        Assert.Equal("B", Cell(terminal, 1, 0).Content);
        Assert.NotNull(Cell(terminal, 0, 0).Placement);
        Assert.NotNull(Cell(terminal, 1, 0).Placement);
    }

    /// <summary>And text typed onto it afterwards leaves the picture there.</summary>
    [Fact]
    public void Text_typed_over_a_negative_z_picture_keeps_it()
    {
        var terminal = WithTwoImages();
        PlaceAt(terminal, 1, 0, 0, z: -1);

        terminal.Write($"{Esc}[1;1HX");

        var cell = Cell(terminal, 0, 0);
        Assert.Equal("X", cell.Content);
        Assert.NotNull(cell.Placement);
        Assert.Equal(-1, cell.Placement!.ZIndex);
    }

    /// <summary>
    /// The tile is carried across unchanged, so the picture does not shuffle under the text as it
    /// is typed on.
    /// </summary>
    [Fact]
    public void Typing_over_a_background_keeps_each_cell_on_its_own_tile()
    {
        var terminal = WithTwoImages();
        PlaceAt(terminal, 1, 0, 0, z: -1);

        terminal.Write($"{Esc}[1;1HXY");

        Assert.Equal(0, Cell(terminal, 0, 0).ImageCol);
        Assert.Equal(1, Cell(terminal, 1, 0).ImageCol);
        Assert.Equal(0, Cell(terminal, 0, 0).ImageRow);
    }

    /// <summary>
    /// A picture in FRONT of the text is still replaced by typing. This is the behaviour every image
    /// had before z existed, and the great majority of pictures still have.
    /// </summary>
    [Fact]
    public void Text_typed_over_a_front_picture_replaces_it()
    {
        var terminal = WithTwoImages();
        PlaceAt(terminal, 1, 0, 0, z: 0);

        terminal.Write($"{Esc}[1;1HX");

        var cell = Cell(terminal, 0, 0);
        Assert.Equal("X", cell.Content);
        Assert.Null(cell.Placement);
    }

    /// <summary>
    /// Erasing clears a background image as well as the text. Erase means the cell is blank, and a
    /// picture still showing through a cleared screen would be a leak, not a feature.
    /// </summary>
    [Fact]
    public void Erasing_clears_a_background_picture_too()
    {
        var terminal = WithTwoImages();
        PlaceAt(terminal, 1, 0, 0, z: -1);
        terminal.Write($"{Esc}[1;1HX");

        terminal.Write($"{Esc}[2J");

        Assert.Null(Cell(terminal, 0, 0).Placement);
        Assert.Equal(" ", Cell(terminal, 0, 0).Content);
    }

    /// <summary>
    /// A wide glyph occupies two cells, and its spacer must keep the background as well -- otherwise
    /// a CJK character punches a hole through the picture behind it.
    /// </summary>
    [Fact]
    public void A_wide_glyph_keeps_the_background_under_both_its_cells()
    {
        var terminal = WithTwoImages();
        PlaceAt(terminal, 1, 0, 0, z: -1);

        terminal.Write($"{Esc}[1;1H\u4E00");   // a full-width ideograph

        Assert.NotNull(Cell(terminal, 0, 0).Placement);
        Assert.NotNull(Cell(terminal, 1, 0).Placement);
        Assert.Equal(1, Cell(terminal, 1, 0).ImageCol);
    }
}
