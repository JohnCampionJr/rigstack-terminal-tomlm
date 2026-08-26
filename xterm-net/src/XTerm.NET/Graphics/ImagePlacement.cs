namespace XTerm.Graphics;

/// <summary>
/// How a tile grid is laid over an image.
/// </summary>
public enum ImageScaling
{
    /// <summary>
    /// One cell shows one cell's worth of pixels, and the tiles at the right and bottom edges are
    /// clipped short. The image keeps its natural size.
    /// </summary>
    /// <remarks>
    /// What Sixel does, and what Kitty does when a placement names no cell box.
    /// </remarks>
    Natural,

    /// <summary>
    /// The source is divided proportionally across the cell box, so the image is stretched or
    /// squeezed to fill exactly the columns and rows asked for.
    /// </summary>
    /// <remarks>
    /// What Kitty's <c>c=</c> and <c>r=</c> keys mean.
    /// </remarks>
    Stretched
}

/// <summary>
/// One appearance of an image on the screen: which part of it, at what size, and under whose
/// identity.
/// </summary>
/// <remarks>
/// <para>Cells reference a placement rather than an image, because Kitty transmits a picture once
/// and may then show it several times, cropped and scaled differently each time. The pixels stay
/// on the <see cref="TerminalImage"/> and are shared; only the view of them differs. A host that
/// caches a texture should therefore key it on <see cref="Image"/>, not on the placement.</para>
/// <para>Sixel makes a placement too, covering the whole image at its natural size. That keeps one
/// path through the renderer rather than two.</para>
/// </remarks>
public sealed class ImagePlacement
{
    /// <summary>The pixels this shows part of.</summary>
    public TerminalImage Image { get; }

    /// <summary>
    /// The client-assigned placement id, or 0 when the client named none.
    /// </summary>
    /// <remarks>
    /// Kitty uses it to delete one appearance of an image without disturbing the others. It is not
    /// an identity for the placement object -- two placements can legitimately share an id of 0 --
    /// so equality is by reference and this is only ever used for lookup.
    /// </remarks>
    public int Id { get; }

    /// <summary>Left edge of the part of the image being shown, in image pixels.</summary>
    public int SourceX { get; }

    /// <summary>Top edge of the part of the image being shown, in image pixels.</summary>
    public int SourceY { get; }

    /// <summary>Width of the part of the image being shown, in image pixels.</summary>
    public int SourceWidth { get; }

    /// <summary>Height of the part of the image being shown, in image pixels.</summary>
    public int SourceHeight { get; }

    /// <summary>How many columns of cells this occupies.</summary>
    public int Cols { get; }

    /// <summary>How many rows of cells this occupies.</summary>
    public int Rows { get; }

    /// <summary>Whether the tiles are natural size or stretched to the cell box.</summary>
    public ImageScaling Scaling { get; }

    /// <summary>
    /// The whole image at its natural size, which is what Sixel produces.
    /// </summary>
    public static ImagePlacement Natural(TerminalImage image, int id = 0)
        => new(image, id, 0, 0, image.PixelWidth, image.PixelHeight,
               image.Cols, image.Rows, ImageScaling.Natural);

    public ImagePlacement(TerminalImage image, int id,
                          int sourceX, int sourceY, int sourceWidth, int sourceHeight,
                          int cols, int rows, ImageScaling scaling)
    {
        Image = image ?? throw new ArgumentNullException(nameof(image));

        if (sourceWidth <= 0 || sourceHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "A placement must show some pixels.");
        if (cols <= 0 || rows <= 0)
            throw new ArgumentOutOfRangeException(nameof(cols), "A placement must cover at least one cell.");

        // Clamped rather than rejected. The crop comes from another process, and a picture shown
        // slightly smaller than asked for is a better answer to a bad rectangle than no picture.
        SourceX = Math.Clamp(sourceX, 0, image.PixelWidth - 1);
        SourceY = Math.Clamp(sourceY, 0, image.PixelHeight - 1);
        SourceWidth = Math.Min(sourceWidth, image.PixelWidth - SourceX);
        SourceHeight = Math.Min(sourceHeight, image.PixelHeight - SourceY);

        Id = id;
        Cols = cols;
        Rows = rows;
        Scaling = scaling;
    }

    /// <summary>
    /// Gets the source rectangle, in image pixels, for one tile of this placement.
    /// </summary>
    /// <remarks>
    /// <para>The two scalings are genuinely different arithmetic, not one formula with a special
    /// case. A 1160 pixel wide image over a 14 pixel cell needs 83 cells, and 83 times 14 is 1162 --
    /// so dividing the source proportionally across those 83 cells gives 13 pixel tiles and
    /// disagrees with the natural layout on every tile, not merely the last. Folding the two
    /// together would quietly resample every Sixel image by a couple of pixels.</para>
    /// <para>Under <see cref="ImageScaling.Natural"/> the edge tiles come back narrower or shorter
    /// than a full cell, and the caller scales the destination to match rather than stretching a
    /// part tile over a whole cell.</para>
    /// </remarks>
    /// <returns>False when the tile lies outside the placement.</returns>
    public bool TryGetTileSource(int tileCol, int tileRow, out int x, out int y, out int width, out int height)
    {
        x = y = width = height = 0;

        if (tileCol < 0 || tileRow < 0 || tileCol >= Cols || tileRow >= Rows)
            return false;

        if (Scaling == ImageScaling.Natural)
        {
            x = SourceX + tileCol * Image.CellWidth;
            y = SourceY + tileRow * Image.CellHeight;
            width = Math.Min(Image.CellWidth, SourceX + SourceWidth - x);
            height = Math.Min(Image.CellHeight, SourceY + SourceHeight - y);
        }
        else
        {
            // Boundaries computed from the tile index both times, so adjacent tiles meet exactly
            // and the rounding never leaves a seam or an overlap.
            var left = SourceX + (int)((long)tileCol * SourceWidth / Cols);
            var right = SourceX + (int)((long)(tileCol + 1) * SourceWidth / Cols);
            var top = SourceY + (int)((long)tileRow * SourceHeight / Rows);
            var bottom = SourceY + (int)((long)(tileRow + 1) * SourceHeight / Rows);

            x = left;
            y = top;
            width = right - left;
            height = bottom - top;
        }

        return width > 0 && height > 0;
    }

    /// <summary>
    /// How much of a cell one tile covers, as a fraction from 0 to 1.
    /// </summary>
    /// <remarks>
    /// A host needs this to size the destination. Under <see cref="ImageScaling.Stretched"/> every
    /// tile fills its cell whatever its source size; under <see cref="ImageScaling.Natural"/> the
    /// edge tiles fall short and must be drawn short, or the picture smears.
    /// </remarks>
    public void GetTileCoverage(int sourceWidth, int sourceHeight, out double cellsWide, out double cellsHigh)
    {
        if (Scaling == ImageScaling.Stretched)
        {
            cellsWide = 1.0;
            cellsHigh = 1.0;
            return;
        }

        cellsWide = Image.CellWidth > 0 ? sourceWidth / (double)Image.CellWidth : 1.0;
        cellsHigh = Image.CellHeight > 0 ? sourceHeight / (double)Image.CellHeight : 1.0;
    }
}
