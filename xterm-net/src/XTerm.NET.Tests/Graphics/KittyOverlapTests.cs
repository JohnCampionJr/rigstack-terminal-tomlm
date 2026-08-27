using XTerm.Options;

namespace XTerm.Tests.Graphics;

/// <summary>
/// Overlapping placements: a cell keeps every picture covering it, not just the winner.
///
/// <para>Two things follow from that, and they are the point of the whole structure. A translucent
/// picture can blend over what it covers, because what it covers is still there to blend with. And
/// deleting or typing over the front picture reveals the one behind it whole -- where before, the
/// covered cells had been overwritten, so the picture underneath came back with a hole through it.
/// The second one is a bug fix rather than a new feature: it bit opaque pictures too.</para>
///
/// <para>Ordering lives in <see cref="KittyZIndexTests"/>; this is about what survives.</para>
/// </summary>
public class KittyOverlapTests
{
    private static Terminal Fresh()
        => new(new TerminalOptions
        {
            Cols = 30,
            Rows = 12,
            CellWidthPixels = 2,
            CellHeightPixels = 3
        });

    /// <summary>A 4x6 picture, which covers two cells by two at the metrics above.</summary>
    private static string Pixels() => Convert.ToBase64String(new byte[4 * 6 * 4]);

    private static XTerm.Buffer.BufferCell Cell(Terminal terminal, int col, int row)
        => terminal.Buffer.Lines[terminal.Buffer.YBase + row]![col];

    private static Terminal WithTwoImages()
    {
        var terminal = Fresh();
        terminal.Write(Esc.Apc("a=t,i=1,f=32,s=4,v=6,q=2", Pixels()));
        terminal.Write(Esc.Apc("a=t,i=2,f=32,s=4,v=6,q=2", Pixels()));
        return terminal;
    }

    private static void PlaceAt(Terminal terminal, uint id, int col, int row, int z)
    {
        terminal.Write(Esc.SetCursorPosition(col, row));
        terminal.Write(Esc.Apc($"a=p,i={id},z={z},C=1,q=2"));
    }

    /// <summary>The placements covering a cell, frontmost first.</summary>
    private static List<XTerm.Graphics.ImagePlacement> Stack(Terminal terminal, int col, int row)
        => Cell(terminal, col, row).ImageLayers().ToList();

    // ---- the stack --------------------------------------------------------------------------------

    /// <summary>Three pictures over one cell are all kept, deepest last.</summary>
    [Fact]
    public void A_cell_keeps_every_picture_covering_it()
    {
        var terminal = WithTwoImages();
        PlaceAt(terminal, 1, 0, 0, z: 1);
        PlaceAt(terminal, 2, 0, 0, z: 5);
        PlaceAt(terminal, 1, 0, 0, z: 3);

        Assert.Equal(new[] { 5, 3, 1 }, Stack(terminal, 0, 0).Select(p => p.ZIndex));
    }

    /// <summary>
    /// At equal z the newer placement goes in front, and the older one stays underneath rather than
    /// being replaced by it.
    /// </summary>
    [Fact]
    public void At_equal_z_the_newer_goes_in_front_and_the_older_stays()
    {
        var terminal = WithTwoImages();
        PlaceAt(terminal, 1, 0, 0, z: 3);
        var older = Cell(terminal, 0, 0).Placement;

        PlaceAt(terminal, 2, 0, 0, z: 3);

        var stack = Stack(terminal, 0, 0);
        Assert.Equal(2, stack.Count);
        Assert.NotSame(older, stack[0]);
        Assert.Same(older, stack[1]);
    }

    /// <summary>
    /// A picture arriving behind one already there is recorded, not dropped. This is the case the old
    /// one-placement cell could not express at all: the lower picture simply never landed.
    /// </summary>
    [Fact]
    public void A_picture_placed_behind_another_is_still_recorded()
    {
        var terminal = WithTwoImages();
        PlaceAt(terminal, 1, 0, 0, z: 5);

        PlaceAt(terminal, 2, 0, 0, z: 1);

        var stack = Stack(terminal, 0, 0);
        Assert.Equal(2, stack.Count);
        Assert.Equal(5, stack[0].ZIndex);
        Assert.Equal(1, stack[1].ZIndex);
    }

    /// <summary>
    /// Nothing stops a client stacking pictures over one spot forever, so the stack is bounded and
    /// the layer dropped is the bottom -- the one furthest from being visible.
    /// </summary>
    [Fact]
    public void The_stack_is_capped_and_the_bottom_layer_goes_first()
    {
        var terminal = WithTwoImages();
        for (int z = 1; z <= XTerm.Buffer.BufferCell.MaxImageLayers + 3; z++)
            PlaceAt(terminal, 1, 0, 0, z: z);

        var stack = Stack(terminal, 0, 0);
        Assert.Equal(XTerm.Buffer.BufferCell.MaxImageLayers, stack.Count);

        // The deepest survivor is the lowest z that was not trimmed away, and the front is the last
        // one placed -- so the window that remains is the top of the stack, not the bottom.
        Assert.Equal(XTerm.Buffer.BufferCell.MaxImageLayers + 3, stack[0].ZIndex);
        Assert.Equal(4, stack[^1].ZIndex);
    }

    // ---- the reveal -------------------------------------------------------------------------------

    /// <summary>Deleting the front picture brings back the one it was covering.</summary>
    [Fact]
    public void Deleting_the_front_picture_reveals_the_one_behind()
    {
        var terminal = WithTwoImages();
        PlaceAt(terminal, 1, 0, 0, z: 1);
        var behind = Cell(terminal, 0, 0).Placement;
        PlaceAt(terminal, 2, 0, 0, z: 5);

        terminal.Write(Esc.Apc("a=d,d=i,i=2,q=2"));

        var cell = Cell(terminal, 0, 0);
        Assert.Same(behind, cell.Placement);
        Assert.Null(cell.Below);
    }

    /// <summary>
    /// The whole of it, not the part that was not covered. A picture two cells wide with something
    /// dropped over its second cell used to come back one cell wide -- the hole this structure
    /// exists to close.
    /// </summary>
    [Fact]
    public void The_revealed_picture_has_no_hole_in_it()
    {
        var terminal = WithTwoImages();
        PlaceAt(terminal, 1, 0, 0, z: 1);          // columns 0-1
        var behind = Cell(terminal, 0, 0).Placement;
        PlaceAt(terminal, 2, 1, 0, z: 5);          // columns 1-2, covering its second cell

        terminal.Write(Esc.Apc("a=d,d=i,i=2,q=2"));

        Assert.Same(behind, Cell(terminal, 0, 0).Placement);
        Assert.Same(behind, Cell(terminal, 1, 0).Placement);

        // And on its own tiles, so the strip is continuous rather than the same column twice.
        Assert.Equal(0, Cell(terminal, 0, 0).ImageCol);
        Assert.Equal(1, Cell(terminal, 1, 0).ImageCol);
    }

    /// <summary>Removing a layer from the middle leaves the ones above and below it alone.</summary>
    [Fact]
    public void Deleting_a_middle_layer_leaves_its_neighbours()
    {
        var terminal = Fresh();
        terminal.Write(Esc.Apc("a=t,i=1,f=32,s=4,v=6,q=2", Pixels()));
        terminal.Write(Esc.Apc("a=t,i=2,f=32,s=4,v=6,q=2", Pixels()));
        terminal.Write(Esc.Apc("a=t,i=3,f=32,s=4,v=6,q=2", Pixels()));

        PlaceAt(terminal, 1, 0, 0, z: 1);
        PlaceAt(terminal, 2, 0, 0, z: 3);
        PlaceAt(terminal, 3, 0, 0, z: 5);

        terminal.Write(Esc.Apc("a=d,d=i,i=2,q=2"));

        Assert.Equal(new[] { 5, 1 }, Stack(terminal, 0, 0).Select(p => p.ZIndex));
    }

    /// <summary>
    /// A delete aimed at a cell takes the pictures at that cell, including ones that happen to be
    /// covered. Selecting only the frontmost would make "delete what is here" depend on what was
    /// stacked over it.
    /// </summary>
    [Fact]
    public void A_positional_delete_reaches_a_covered_picture()
    {
        var terminal = WithTwoImages();
        PlaceAt(terminal, 1, 0, 0, z: 1);
        PlaceAt(terminal, 2, 0, 0, z: 5);

        terminal.Write(Esc.Apc("a=d,d=p,x=1,y=1,q=2"));   // one-based: cell 0,0

        Assert.Null(Cell(terminal, 0, 0).Placement);
    }

    // ---- against the text -------------------------------------------------------------------------

    /// <summary>
    /// A picture in front of the text covers it and the background picture under it, and keeps both
    /// of those distinct: the character goes, the background layer stays.
    /// </summary>
    [Fact]
    public void A_front_picture_covers_the_text_but_not_the_background_under_it()
    {
        var terminal = WithTwoImages();
        PlaceAt(terminal, 1, 0, 0, z: -1);
        terminal.Write(Esc.SetCursorPosition(0, 0) + "X");

        PlaceAt(terminal, 2, 0, 0, z: 2);

        var cell = Cell(terminal, 0, 0);
        Assert.Equal(" ", cell.Content);
        Assert.Equal(new[] { 2, -1 }, cell.ImageLayers().Select(p => p.ZIndex));
    }

    /// <summary>
    /// Typing keeps the layers behind the text and drops the ones in front, which is the same rule
    /// as before applied to a stack rather than to a single placement.
    /// </summary>
    [Fact]
    public void Typing_keeps_the_background_layers_and_drops_the_front_ones()
    {
        var terminal = WithTwoImages();
        PlaceAt(terminal, 1, 0, 0, z: -2);
        PlaceAt(terminal, 2, 0, 0, z: -1);
        PlaceAt(terminal, 1, 0, 0, z: 4);

        terminal.Write(Esc.SetCursorPosition(0, 0) + "X");

        var cell = Cell(terminal, 0, 0);
        Assert.Equal("X", cell.Content);
        Assert.Equal(new[] { -1, -2 }, cell.ImageLayers().Select(p => p.ZIndex));
    }

    /// <summary>
    /// Several background pictures under one character all survive being typed over. One layer
    /// carried across would have been enough for the single-placement case and is not enough here.
    /// </summary>
    [Fact]
    public void Two_background_pictures_both_survive_the_text()
    {
        var terminal = WithTwoImages();
        PlaceAt(terminal, 1, 0, 0, z: -5);
        PlaceAt(terminal, 2, 0, 0, z: -1);

        terminal.Write(Esc.SetCursorPosition(0, 0) + "X");

        Assert.Equal(new[] { -1, -5 }, Stack(terminal, 0, 0).Select(p => p.ZIndex));
    }

    /// <summary>
    /// Deleting every placement takes the pictures and leaves the text, the same as deleting one by
    /// id does.
    /// </summary>
    /// <remarks>
    /// <c>d=a</c> reached the cells through the helper a resize uses, which blanks them -- correct
    /// for a picture in front of the text, whose character was only ever a placeholder, and
    /// destructive for a background one, whose character is whatever the user typed onto it. The
    /// difference is invisible until something is drawn behind text, which is how it survived: every
    /// other image cell holds a space, and blanking a space changes nothing.
    /// </remarks>
    [Fact]
    public void Deleting_every_placement_leaves_the_text_on_a_background()
    {
        var terminal = WithTwoImages();
        PlaceAt(terminal, 1, 0, 0, z: -1);
        terminal.Write(Esc.SetCursorPosition(0, 0) + "X");

        terminal.Write(Esc.Apc("a=d,d=A,q=2"));

        var cell = Cell(terminal, 0, 0);
        Assert.Null(cell.Placement);
        Assert.Equal("X", cell.Content);
    }

    /// <summary>And a picture in front of the text still leaves a blank behind, as it always did.</summary>
    [Fact]
    public void Deleting_every_placement_blanks_a_foreground_picture()
    {
        var terminal = WithTwoImages();
        PlaceAt(terminal, 1, 0, 0, z: 0);

        terminal.Write(Esc.Apc("a=d,d=A,q=2"));

        var cell = Cell(terminal, 0, 0);
        Assert.Null(cell.Placement);
        Assert.Equal(" ", cell.Content);
        Assert.Equal(1, cell.Width);
    }

    /// <summary>Erasing takes the whole stack; a picture showing through a cleared screen is a leak.</summary>
    [Fact]
    public void Erasing_clears_every_layer()
    {
        var terminal = WithTwoImages();
        PlaceAt(terminal, 1, 0, 0, z: -1);
        PlaceAt(terminal, 2, 0, 0, z: 5);

        terminal.Write(Esc.ClearScreen);

        var cell = Cell(terminal, 0, 0);
        Assert.Null(cell.Placement);
        Assert.Null(cell.Below);
    }

    /// <summary>
    /// Deleting the picture in front of a character leaves the character, because a background
    /// picture never owned it. The front picture had already blanked what was there when it landed,
    /// so there is nothing to restore -- but the text of a cell that only ever had a background must
    /// not be swept away with it.
    /// </summary>
    [Fact]
    public void Deleting_a_background_picture_leaves_the_text()
    {
        var terminal = WithTwoImages();
        PlaceAt(terminal, 1, 0, 0, z: -1);
        terminal.Write(Esc.SetCursorPosition(0, 0) + "X");

        terminal.Write(Esc.Apc("a=d,d=i,i=1,q=2"));

        var cell = Cell(terminal, 0, 0);
        Assert.Null(cell.Placement);
        Assert.Equal("X", cell.Content);
    }

    // ---- the cell itself --------------------------------------------------------------------------

    /// <summary>
    /// A cell covered by one picture allocates no chain at all. The structure is there for the rare
    /// overlap and must not cost the common case anything.
    /// </summary>
    [Fact]
    public void One_picture_over_a_cell_builds_no_chain()
    {
        var terminal = WithTwoImages();
        PlaceAt(terminal, 1, 0, 0, z: 0);

        var cell = Cell(terminal, 0, 0);
        Assert.NotNull(cell.Placement);
        Assert.Null(cell.Below);
        Assert.False(cell.IsLayered);
    }

    /// <summary>
    /// Two cells showing the same pictures at the same tiles are equal even though their chains were
    /// built separately -- otherwise a renderer comparing cells would repaint the line every frame.
    /// </summary>
    [Fact]
    public void Cells_with_matching_stacks_are_equal()
    {
        var terminal = WithTwoImages();
        PlaceAt(terminal, 1, 0, 0, z: 1);
        PlaceAt(terminal, 2, 0, 0, z: 5);

        var cell = Cell(terminal, 0, 0);
        var rebuilt = new XTerm.Buffer.BufferCell(" ", 1, cell.Attributes);
        foreach (var placement in cell.ImageLayers().Reverse())
            rebuilt.AddImageLayer(placement, cell.TryGetTile(placement, out var tile) ? tile : 0);

        Assert.Equal(cell, rebuilt);
    }
}
