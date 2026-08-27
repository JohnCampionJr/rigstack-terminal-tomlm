namespace XTerm.Graphics;

/// <summary>
/// One picture showing through a cell, underneath the one the cell holds inline.
/// </summary>
/// <remarks>
/// <para>A cell shows one picture almost always, and that one lives inline on the cell as it always
/// has. This is what the rare overlapping cell hangs off it: the second and any further placements
/// covering the same cell, ordered downwards, so nothing is destroyed merely by being covered.</para>
/// <para>Immutable, and deliberately. Cells are structs copied by value all over the buffer --
/// scrolling, resizing, the render cache -- and a chain that could be edited in place would be
/// shared by every copy of a cell that was ever made. Inserting and removing therefore rebuild the
/// part of the chain above the change and share the untouched tail, which is a few nodes on a
/// structure that is rarely more than two deep.</para>
/// </remarks>
public sealed class CellImageLayer
{
    /// <summary>The appearance this layer shows part of.</summary>
    public ImagePlacement Placement { get; }

    /// <summary>Which piece of it, packed the same way <c>BufferCell.ImageTile</c> is.</summary>
    public int Tile { get; }

    /// <summary>The next layer down, or null when this is the bottom.</summary>
    public CellImageLayer? Below { get; }

    public CellImageLayer(ImagePlacement placement, int tile, CellImageLayer? below = null)
    {
        Placement = placement ?? throw new ArgumentNullException(nameof(placement));
        Tile = tile;
        Below = below;
    }

    /// <summary>How many layers hang below this one, including it.</summary>
    public int Depth
    {
        get
        {
            var count = 0;
            for (var layer = this; layer is not null; layer = layer.Below)
                count++;
            return count;
        }
    }

    /// <summary>
    /// Whether one placement belongs in front of another.
    /// </summary>
    /// <remarks>
    /// <para>Z-index first, and the sequence number to break a tie -- which is Kitty's rule that at
    /// equal z the placement made later is drawn on top. Without the tiebreak the order between two
    /// placements at the same z would depend on the order they happened to be visited in, and a
    /// renderer sorting a line would be free to disagree with the buffer about which is in
    /// front.</para>
    /// <para>A total order, so it can be used both to insert into a cell's stack and to sort the
    /// distinct placements on a line. Those two have to agree or a picture drawn per-line would
    /// composite differently from the way the cells are stacked.</para>
    /// </remarks>
    public static bool IsInFrontOf(ImagePlacement candidate, ImagePlacement existing)
        => candidate.ZIndex != existing.ZIndex
            ? candidate.ZIndex > existing.ZIndex
            : candidate.Sequence > existing.Sequence;
}
