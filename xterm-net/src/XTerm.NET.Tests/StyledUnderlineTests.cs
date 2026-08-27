using System.Runtime.CompilerServices;
using XTerm;
using XTerm.Buffer;
using XTerm.Common;
using XTerm.Options;

namespace XTerm.Tests;

/// <summary>
/// Styled underlines (SGR 4:1–4:5, 21) and underline colour (SGR 58/59).
/// </summary>
/// <remarks>
/// The squiggly underline an LSP puts under an error. The style enum and sub-parameter parsing
/// already existed — the sub-parameters were being read and then dropped, so a program asking for a
/// curly underline got a straight one.
/// </remarks>
public class StyledUnderlineTests
{
    private const string Esc = "\u001b";

    private static Terminal Fresh() => new(new TerminalOptions { Cols = 20, Rows = 3 });

    private static BufferCell FirstCell(Terminal terminal)
        => terminal.Buffer.Lines[terminal.Buffer.YBase]![0];

    [Theory]
    [InlineData("4", UnderlineStyle.Single)]
    [InlineData("4:0", UnderlineStyle.None)]
    [InlineData("4:1", UnderlineStyle.Single)]
    [InlineData("4:2", UnderlineStyle.Double)]
    [InlineData("4:3", UnderlineStyle.Curly)]
    [InlineData("4:4", UnderlineStyle.Dotted)]
    [InlineData("4:5", UnderlineStyle.Dashed)]
    [InlineData("21", UnderlineStyle.Double)]
    public void Sgr_selects_the_underline_style(string sgr, UnderlineStyle expected)
    {
        var terminal = Fresh();
        terminal.Write($"{Esc}[{sgr}mx");

        Assert.Equal(expected, FirstCell(terminal).Attributes.GetUnderlineStyle());
    }

    /// <summary>
    /// A style nobody has defined is still an underline. Drawing a plain one is closer to what the
    /// program asked for than drawing nothing at all.
    /// </summary>
    [Fact]
    public void An_unknown_style_still_underlines()
    {
        var terminal = Fresh();
        terminal.Write($"{Esc}[4:9mx");

        Assert.Equal(UnderlineStyle.Single, FirstCell(terminal).Attributes.GetUnderlineStyle());
    }

    /// <summary>
    /// The style is the single source of truth, so a cell underlined by any of these reports it.
    /// Keeping a separate flag beside the style is how a cell ends up underlined by one and not
    /// the other.
    /// </summary>
    [Fact]
    public void IsUnderline_follows_the_style()
    {
        var terminal = Fresh();

        terminal.Write($"{Esc}[4:3mx");
        Assert.True(FirstCell(terminal).Attributes.IsUnderline());

        terminal.Write($"{Esc}[24m{Esc}[1;1Hy");
        Assert.False(FirstCell(terminal).Attributes.IsUnderline());
        Assert.Equal(UnderlineStyle.None, FirstCell(terminal).Attributes.GetUnderlineStyle());
    }

    // ---- colour ---------------------------------------------------------------------------------

    [Fact]
    public void Sgr58_sets_a_truecolor_underline_as_subparameters()
    {
        var terminal = Fresh();
        terminal.Write($"{Esc}[4:3;58:2::255:0:0mx");

        var attr = FirstCell(terminal).Attributes;
        Assert.True(attr.TryGetUnderlineColor(out var color, out var mode));
        Assert.Equal((255 << 16) | (0 << 8) | 0, color);
        Assert.Equal(1, mode);
        Assert.Equal(UnderlineStyle.Curly, attr.GetUnderlineStyle());
    }

    /// <summary>
    /// Both spellings are in use, and a terminal that takes only one looks broken to half its
    /// callers.
    /// </summary>
    [Fact]
    public void Sgr58_also_accepts_separate_parameters()
    {
        var terminal = Fresh();
        terminal.Write($"{Esc}[58;2;0;128;255mx");

        Assert.True(FirstCell(terminal).Attributes.TryGetUnderlineColor(out var color, out var mode));
        Assert.Equal((0 << 16) | (128 << 8) | 255, color);
        Assert.Equal(1, mode);
    }

    [Fact]
    public void Sgr58_accepts_an_indexed_colour()
    {
        var terminal = Fresh();
        terminal.Write($"{Esc}[58:5:196mx");

        Assert.True(FirstCell(terminal).Attributes.TryGetUnderlineColor(out var color, out var mode));
        Assert.Equal(196, color);
        Assert.Equal(0, mode);
    }

    [Fact]
    public void Sgr59_puts_the_underline_back_to_the_foreground()
    {
        var terminal = Fresh();
        terminal.Write($"{Esc}[58:2::255:0:0m{Esc}[59mx");

        Assert.False(FirstCell(terminal).Attributes.TryGetUnderlineColor(out _, out _));
    }

    [Fact]
    public void A_reset_clears_the_style_and_the_colour()
    {
        var terminal = Fresh();
        terminal.Write($"{Esc}[4:3;58:2::255:0:0m{Esc}[0mx");

        var attr = FirstCell(terminal).Attributes;
        Assert.Equal(UnderlineStyle.None, attr.GetUnderlineStyle());
        Assert.False(attr.TryGetUnderlineColor(out _, out _));
    }

    /// <summary>
    /// The same colour used twice is one entry, which is what keeps twenty bits of id enough.
    /// </summary>
    [Fact]
    public void The_same_colour_interns_once()
    {
        var terminal = Fresh();

        terminal.Write($"{Esc}[58:2::10:20:30mx");
        var first = FirstCell(terminal).Attributes.GetUnderlineColorId();

        terminal.Write($"{Esc}[0m{Esc}[58:2::10:20:30m{Esc}[1;1Hy");
        var second = FirstCell(terminal).Attributes.GetUnderlineColorId();

        Assert.Equal(first, second);
        Assert.NotEqual(0, first);
    }

    // ---- the reason this is stored as an id ------------------------------------------------------

    /// <summary>
    /// The whole feature had to fit in bits the cell already owned.
    /// </summary>
    /// <remarks>
    /// A full RGB underline colour plus its mode is more bits than were left, and growing
    /// AttributeData grows every cell in the buffer — the thing measured as costing most on fills.
    /// So the cell carries an interned id, and this asserts the cost of the feature is zero.
    /// </remarks>
    [Fact]
    public void The_cell_did_not_grow()
    {
        Assert.Equal(12, Unsafe.SizeOf<AttributeData>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<AttributeData>());
    }

    /// <summary>
    /// Style and colour live in the same int as the boolean attributes and must not disturb them.
    /// </summary>
    [Fact]
    public void The_style_and_colour_do_not_disturb_the_other_attributes()
    {
        var terminal = Fresh();
        terminal.Write($"{Esc}[1;3;4:3;58:2::255:0:0;9mx");

        var attr = FirstCell(terminal).Attributes;
        Assert.True(attr.IsBold());
        Assert.True(attr.IsItalic());
        Assert.True(attr.IsStrikethrough());
        Assert.Equal(UnderlineStyle.Curly, attr.GetUnderlineStyle());
        Assert.True(attr.TryGetUnderlineColor(out _, out _));
    }
}
