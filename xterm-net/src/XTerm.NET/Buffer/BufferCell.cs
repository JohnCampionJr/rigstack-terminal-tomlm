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
    /// The image this cell shows a piece of, or null for an ordinary text cell.
    /// </summary>
    /// <remarks>
    /// <para>Living on the cell rather than in a separate overlay is what makes an image behave
    /// like terminal content. Printing a character builds a whole new cell, so the image reference
    /// goes with the old one; erasing fills with a blank cell, which has no image; scrolling moves
    /// whole lines, so the pieces travel together. None of that needed code -- it is what a struct
    /// copied by value already does.</para>
    /// <para>The image itself is shared by every cell covering it, and dies with the last one, so
    /// a picture scrolled off the end of the scrollback is collected without an eviction pass.</para>
    /// </remarks>
    public TerminalImage? Image = null;

    /// <summary>
    /// Which piece of <see cref="Image"/> this cell shows, packed as (row &lt;&lt; 16) | column.
    /// Meaningless when <see cref="Image"/> is null.
    /// </summary>
    /// <remarks>
    /// Packed because the reference above already forces the struct onto an eight-byte boundary,
    /// which leaves four bytes of padding this fits into for free. Two separate ints would not.
    /// </remarks>
    public int ImageTile = 0;

    /// <summary>The column of <see cref="Image"/>'s tile grid that this cell shows.</summary>
    public readonly int ImageCol => ImageTile & 0xFFFF;

    /// <summary>The row of <see cref="Image"/>'s tile grid that this cell shows.</summary>
    public readonly int ImageRow => (ImageTile >> 16) & 0xFFFF;

    /// <summary>Whether this cell shows part of an image.</summary>
    public readonly bool IsImage => Image is not null;

    /// <summary>Packs tile coordinates for <see cref="ImageTile"/>.</summary>
    public static int PackTile(int col, int row) => ((row & 0xFFFF) << 16) | (col & 0xFFFF);

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
        // Image identity is part of cell equality, and not only for tests: renderers coalesce
        // adjacent cells into a single run by comparing them, and two cells showing different
        // pieces of a picture are not interchangeable however alike their text is.
        return Content == other.Content &&
               Width == other.Width &&
               Attributes.Equals(other.Attributes) &&
               CodePoint == other.CodePoint &&
               ReferenceEquals(Image, other.Image) &&
               (Image is null || ImageTile == other.ImageTile);
    }

    public override bool Equals(object? obj)
    {
        return obj is BufferCell other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Content, Width, Attributes, CodePoint,
            Image is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Image),
            Image is null ? 0 : ImageTile);
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
