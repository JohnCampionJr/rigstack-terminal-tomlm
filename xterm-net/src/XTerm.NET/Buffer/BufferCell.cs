using System.Diagnostics;
using System.Text;
using XTerm.Common;
using XTerm.Graphics;

namespace XTerm.Buffer;

/// <summary>
/// Represents a single cell in the terminal buffer.
/// Each cell contains a character, width, and attributes.
/// </summary>
[DebuggerDisplay("'{Content}'  [{Width}, {Attributes}, {CodePoint}]{Image != null ? \" image\" : \"\"}")]
public struct BufferCell : IEquatable<BufferCell>
{
    public string Content = String.Empty;
    public int Width = 0;
    public AttributeData Attributes = AttributeData.Default;
    public int CodePoint = 0;

    /// <summary>
    /// The image appearance this cell shows a piece of, or null for an ordinary text cell.
    /// </summary>
    /// <remarks>
    /// <para>Living on the cell rather than in a separate overlay is what makes an image behave
    /// like terminal content. Printing a character builds a whole new cell, so the reference goes
    /// with the old one; erasing fills with a blank cell, which has none; scrolling moves whole
    /// lines, so the pieces travel together. None of that needed code -- it is what a struct copied
    /// by value already does.</para>
    /// <para>A placement rather than an image, because one picture can be shown several times at
    /// different crops and scales. The pixels are shared between them and die with the last cell
    /// holding any of them, so a picture scrolled off the end of the scrollback is collected
    /// without an eviction pass.</para>
    /// </remarks>
    public ImagePlacement? Placement = null;

    /// <summary>
    /// Which piece of <see cref="Placement"/> this cell shows, packed as (row &lt;&lt; 16) | column.
    /// Meaningless when <see cref="Placement"/> is null.
    /// </summary>
    /// <remarks>
    /// Packed because the reference above already forces the struct onto an eight-byte boundary,
    /// which leaves four bytes of padding this fits into for free. Two separate ints would not.
    /// </remarks>
    public int ImageTile = 0;

    /// <summary>
    /// Any further pictures covering this cell, below <see cref="Placement"/> and ordered downwards,
    /// or null for the overwhelmingly common cell that is covered by at most one.
    /// </summary>
    /// <remarks>
    /// <para>Kitty lets placements overlap, and the one in front does not destroy the one behind: a
    /// translucent picture blends over what it covers, and deleting the front one has to reveal the
    /// back one whole rather than leave a hole punched through it. Neither is expressible while a
    /// cell remembers only the winner.</para>
    /// <para>Kept as a separate reference rather than moving the whole stack off the cell so that
    /// the common case pays nothing but the field: one picture over a cell still reads straight off
    /// <see cref="Placement"/> and <see cref="ImageTile"/> with no allocation and no indirection, and
    /// only genuinely overlapping cells build a chain. Null whenever <see cref="Placement"/> is null
    /// -- there is no layer below nothing.</para>
    /// </remarks>
    public CellImageLayer? Below = null;

    /// <summary>
    /// How many pictures may cover one cell before the bottom one is dropped.
    /// </summary>
    /// <remarks>
    /// A bound rather than a protocol rule: nothing stops a client placing pictures over one spot
    /// forever, and every layer is retained memory for pixels that cannot be seen through the ones
    /// above. Deep enough that no plausible arrangement reaches it, and the layer dropped is the
    /// bottom -- the one furthest from being visible.
    /// </remarks>
    public const int MaxImageLayers = 8;

    /// <summary>
    /// The image whose pixels this cell shows, or null for an ordinary text cell.
    /// </summary>
    /// <remarks>
    /// A view onto <see cref="Placement"/>, kept because it is what a host caches its texture
    /// against: two placements of one picture share these pixels and should share one upload.
    /// </remarks>
    public readonly TerminalImage? Image => Placement?.Image;

    /// <summary>The column of <see cref="Placement"/>'s tile grid that this cell shows.</summary>
    public readonly int ImageCol => ImageTile & 0xFFFF;

    /// <summary>The row of <see cref="Placement"/>'s tile grid that this cell shows.</summary>
    public readonly int ImageRow => (ImageTile >> 16) & 0xFFFF;

    /// <summary>Whether this cell shows part of an image.</summary>
    public readonly bool IsImage => Placement is not null;

    /// <summary>Whether more than one picture covers this cell.</summary>
    public readonly bool IsLayered => Below is not null;

    /// <summary>
    /// The picture furthest back in this cell, which is <see cref="Placement"/> unless something
    /// overlaps it.
    /// </summary>
    /// <remarks>
    /// A renderer needs this to decide who paints the cell's background. The background belongs to
    /// the CELL -- it is what a transparent picture lets through -- so it goes down once, under
    /// everything, and painting it again with an upper layer would erase the layers below instead of
    /// letting them blend.
    /// </remarks>
    public readonly ImagePlacement? BottomPlacement
    {
        get
        {
            if (Below is null)
                return Placement;

            var layer = Below;
            while (layer.Below is not null)
                layer = layer.Below;
            return layer.Placement;
        }
    }

    /// <summary>
    /// Every picture covering this cell, frontmost first.
    /// </summary>
    /// <remarks>
    /// Allocates, so it is for callers that are already walking a whole line rather than for the
    /// per-cell path a renderer takes sixty times a second.
    /// </remarks>
    public readonly IEnumerable<ImagePlacement> ImageLayers()
    {
        if (Placement is null)
            yield break;

        yield return Placement;
        for (var layer = Below; layer is not null; layer = layer.Below)
            yield return layer.Placement;
    }

    /// <summary>Packs tile coordinates for <see cref="ImageTile"/>.</summary>
    public static int PackTile(int col, int row) => ((row & 0xFFFF) << 16) | (col & 0xFFFF);

    /// <summary>
    /// Finds the piece of one particular placement that this cell shows.
    /// </summary>
    /// <remarks>
    /// How a renderer walks a line for one placement at a time, which is what lets it draw the
    /// stack from the bottom up and still coalesce each layer into strips. The frontmost is checked
    /// first and is nearly always the answer, so an unlayered cell costs one reference comparison.
    /// </remarks>
    public readonly bool TryGetTile(ImagePlacement placement, out int tile)
    {
        if (ReferenceEquals(Placement, placement))
        {
            tile = ImageTile;
            return true;
        }

        for (var layer = Below; layer is not null; layer = layer.Below)
        {
            if (ReferenceEquals(layer.Placement, placement))
            {
                tile = layer.Tile;
                return true;
            }
        }

        tile = 0;
        return false;
    }

    /// <summary>
    /// Adds a picture to this cell's stack, in front of or behind what is already there according
    /// to z-index and age.
    /// </summary>
    /// <remarks>
    /// The insert is ordered rather than the placement simply replacing what it covers, which is
    /// the whole of what "overlapping" means here. A placement that lands behind an existing one is
    /// still recorded, so it is there to be revealed when the front one is deleted or scrolls away.
    /// </remarks>
    public void AddImageLayer(ImagePlacement placement, int tile)
    {
        if (placement is null)
            return;

        if (Placement is null)
        {
            Placement = placement;
            ImageTile = tile;
            Below = null;
            return;
        }

        if (CellImageLayer.IsInFrontOf(placement, Placement))
        {
            Below = Trim(new CellImageLayer(Placement, ImageTile, Below), MaxImageLayers - 1);
            Placement = placement;
            ImageTile = tile;
            return;
        }

        Below = Trim(Insert(Below, placement, tile), MaxImageLayers - 1);
    }

    /// <summary>
    /// Removes every layer whose placement matches, closing the gap.
    /// </summary>
    /// <remarks>
    /// Removing the frontmost promotes whatever was under it, which is the reveal that makes an
    /// overlap non-destructive. Removing from the middle leaves both neighbours alone.
    /// </remarks>
    /// <returns>True if anything was removed, which is also the signal to repaint.</returns>
    public bool RemoveImageLayers(Func<ImagePlacement, bool> predicate)
    {
        if (Placement is null)
            return false;

        var removed = false;
        var below = Remove(Below, predicate, ref removed);

        if (predicate(Placement))
        {
            removed = true;
            if (below is null)
            {
                Placement = null;
                ImageTile = 0;
                Below = null;
                return true;
            }

            Placement = below.Placement;
            ImageTile = below.Tile;
            below = below.Below;
        }

        Below = below;
        return removed;
    }

    /// <summary>Drops every picture covering this cell, leaving its text alone.</summary>
    public void ClearImageLayers()
    {
        Placement = null;
        ImageTile = 0;
        Below = null;
    }

    /// <summary>
    /// Removes matching pictures and tidies the cell afterwards.
    /// </summary>
    /// <remarks>
    /// <para>The tidy-up is the part a new delete path gets wrong, which is why it lives here rather
    /// than at each call site. A placement in FRONT of the text blanked the character when it
    /// landed, so once the picture goes there is nothing to restore and the cell should read as the
    /// blank it already is. A BACKGROUND one never owned the character -- that was typed onto the
    /// picture afterwards -- and taking it away with the picture destroys the user's text.</para>
    /// <para>Getting that wrong is invisible until something is actually drawn behind text, because
    /// until then every image cell holds a space and blanking a space changes nothing.</para>
    /// </remarks>
    /// <returns>True if anything was removed, which is also the signal to repaint.</returns>
    public bool RemoveImages(Func<ImagePlacement, bool> predicate)
    {
        if (Placement is null)
            return false;

        var characterWasThePicture = Placement.ZIndex >= 0;

        if (!RemoveImageLayers(predicate))
            return false;

        if (Placement is null && characterWasThePicture)
        {
            Content = " ";
            Width = 1;
            CodePoint = 0x20;
        }

        return true;
    }

    /// <summary>
    /// Keeps only the layers that sit behind the text, dropping the rest.
    /// </summary>
    /// <remarks>
    /// What printing a character over a picture does. A negative z-index means the client asked for
    /// the picture to stay under whatever text lands on it; at zero or above the picture is in front
    /// of the text and being printed over is the end of it.
    /// </remarks>
    /// <returns>True if anything is still showing through the cell.</returns>
    public bool KeepOnlyBackgroundLayers()
    {
        RemoveImageLayers(static p => p.ZIndex >= 0);
        return Placement is not null;
    }

    private static CellImageLayer Insert(CellImageLayer? chain, ImagePlacement placement, int tile)
    {
        if (chain is null || CellImageLayer.IsInFrontOf(placement, chain.Placement))
            return new CellImageLayer(placement, tile, chain);

        return new CellImageLayer(chain.Placement, chain.Tile, Insert(chain.Below, placement, tile));
    }

    private static CellImageLayer? Remove(CellImageLayer? chain, Func<ImagePlacement, bool> predicate,
                                          ref bool removed)
    {
        if (chain is null)
            return null;

        var below = Remove(chain.Below, predicate, ref removed);

        if (predicate(chain.Placement))
        {
            removed = true;
            return below;
        }

        return ReferenceEquals(below, chain.Below) ? chain : new CellImageLayer(chain.Placement, chain.Tile, below);
    }

    private static CellImageLayer? Trim(CellImageLayer? chain, int remaining)
    {
        if (chain is null)
            return null;
        if (remaining <= 0)
            return null;

        var below = Trim(chain.Below, remaining - 1);
        return ReferenceEquals(below, chain.Below) ? chain : new CellImageLayer(chain.Placement, chain.Tile, below);
    }

    public static BufferCell Empty => new BufferCell();

    public static BufferCell Space => new BufferCell
    {
        Content = " ",
        Width = 1,
        Attributes = AttributeData.Default,
        CodePoint = 0x20
    };

    public BufferCell()
    {
        Content = String.Empty;
        Attributes = AttributeData.Default;
    }
    public BufferCell(string content, int width, AttributeData attributes)
    {
        Content = content;
        Width = width;
        Attributes = attributes;
        CodePoint = content.Length > 0 ? char.ConvertToUtf32(content, 0) : 0;
    }

    public BufferCell(int codePoint, int width, AttributeData attributes)
    {
        CodePoint = codePoint;
        Width = width;
        Attributes = attributes;
        Content = char.ConvertFromUtf32(codePoint);
    }

    public bool IsEmpty() => CodePoint == Empty.CodePoint;

    public bool IsSpace() => CodePoint == Space.CodePoint;

    public bool Equals(BufferCell other)
    {
        // Placement identity is part of cell equality, and not only for tests: renderers coalesce
        // adjacent cells into a single run by comparing them, and two cells showing different
        // pieces of a picture are not interchangeable however alike their text is.
        //
        // The PLACEMENT, not the image. Two appearances of one picture share its pixels, so
        // comparing images would call cells from different placements equal and let a renderer run
        // a strip straight across the join between them.
        return Content == other.Content &&
               Width == other.Width &&
               Attributes.Equals(other.Attributes) &&
               CodePoint == other.CodePoint &&
               ReferenceEquals(Placement, other.Placement) &&
               (Placement is null || ImageTile == other.ImageTile) &&
               SameLayers(Below, other.Below);
    }

    /// <summary>
    /// Compares two layer chains by what they show rather than by node identity.
    /// </summary>
    /// <remarks>
    /// Structural, because the chains are rebuilt above the point of any insert or removal: two
    /// cells showing exactly the same pictures at the same tiles can easily hold different nodes,
    /// and calling those cells unequal would repaint a line every frame for nothing. Short by
    /// construction -- <see cref="MaxImageLayers"/> bounds it -- and null almost always, which is
    /// the case that returns immediately.
    /// </remarks>
    private static bool SameLayers(CellImageLayer? left, CellImageLayer? right)
    {
        while (left is not null && right is not null)
        {
            if (!ReferenceEquals(left.Placement, right.Placement) || left.Tile != right.Tile)
                return false;

            left = left.Below;
            right = right.Below;
        }

        return left is null && right is null;
    }

    public override bool Equals(object? obj)
    {
        return obj is BufferCell other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Content, Width, Attributes, CodePoint,
            Placement is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Placement),
            Placement is null ? 0 : ImageTile);
    }

    public static bool operator ==(BufferCell left, BufferCell right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(BufferCell left, BufferCell right)
    {
        return !left.Equals(right);
    }
}
