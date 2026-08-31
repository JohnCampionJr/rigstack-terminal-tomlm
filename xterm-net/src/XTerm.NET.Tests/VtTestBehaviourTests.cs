using XTerm.Options;

namespace XTerm.Tests;

/// <summary>
/// Behaviour that vttest 2.7 (20230201) exercises and this emulator gets RIGHT.
///
/// <para>These are guards, not discoveries. Each was probed while sweeping vttest, several of them
/// because they looked wrong at first and turned out not to be — margins clipping what a screen
/// dump made look like lost text, reverse wraparound that only engages on a line which actually
/// wrapped. Writing them down is what stops the next reader re-deriving the same false alarm.</para>
///
/// <para>None of these can be seen in a screen dump: they are attributes, cell protection, cursor
/// mechanics and mode gating. That is why they are tests rather than a diff against another
/// terminal — the comparison that found the rest of these cases is blind to every one of them.</para>
///
/// <para>vttest is Copyright 1996-2022 by Thomas E. Dickey, under an X11-style licence. No vttest
/// source is copied here.</para>
/// </summary>
public class VtTestBehaviourTests
{
    private const string Esc = "\u001b";

    private static Terminal Sized(int cols = 40, int rows = 6) =>
        new(new TerminalOptions { Cols = cols, Rows = rows });

    private static string BackgroundOf(Terminal terminal, int row, int col) =>
        terminal.Buffer.Lines[row]![col].Attributes.Bg.ToString();

    /// <summary>
    /// BCE: an erase fills with the CURRENT background, not the default one. vttest menu 11.6.4
    /// and 11.6.5, which a text-only comparison cannot see at all.
    /// </summary>
    [Fact]
    public void Erases_fill_with_the_current_background()
    {
        var terminal = Sized(20, 5);

        terminal.Write($"{Esc}[44m{Esc}[2J");                       // blue, erase display
        Assert.Equal("4", BackgroundOf(terminal, 0, 0));
        Assert.Equal("4", BackgroundOf(terminal, 4, 19));

        terminal.Write($"{Esc}[H{Esc}[41m{Esc}[K");                 // red, erase to end of line
        Assert.Equal("1", BackgroundOf(terminal, 0, 0));

        terminal.Write($"{Esc}[42m{Esc}[5X");                       // green, erase 5 characters
        Assert.Equal("2", BackgroundOf(terminal, 0, 0));
    }

    /// <summary>
    /// SGR 22 clears bold AND dim. They share one code, and clearing only one of them is the
    /// classic way this goes wrong. vttest menu 11.6.9.
    /// </summary>
    [Fact]
    public void SGR_22_clears_both_bold_and_dim()
    {
        var terminal = Sized(20, 3);

        terminal.Write($"{Esc}[1;2mX");
        var bright = terminal.Buffer.Lines[0]![0].Attributes;
        Assert.True(bright.IsBold());
        Assert.True(bright.IsDim());

        terminal.Write($"{Esc}[22mY");
        var cleared = terminal.Buffer.Lines[0]![1].Attributes;
        Assert.False(cleared.IsBold());
        Assert.False(cleared.IsDim());
    }

    /// <summary>DECSCA marks cells that DECSEL must not erase; ECH is not selective and erases them.</summary>
    [Fact]
    public void Selective_erase_respects_protection_and_ECH_does_not()
    {
        var terminal = Sized(20, 3);

        terminal.Write($"{Esc}[2J{Esc}[H{Esc}[1\"qPROT{Esc}[0\"qPLAIN");
        terminal.Write($"{Esc}[H{Esc}[?0K");                        // DECSEL to end of line
        Assert.Equal("PROT", terminal.GetLine(0));

        terminal.Write($"{Esc}[2J{Esc}[H{Esc}[1\"qPROT{Esc}[0\"q");
        terminal.Write($"{Esc}[H{Esc}[4X");                         // ECH over the same cells
        Assert.Equal(string.Empty, terminal.GetLine(0));
    }

    /// <summary>
    /// Left/right margins clip editing and wrap text, and the text outside them is left alone.
    /// </summary>
    /// <remarks>
    /// The wrap is the part worth pinning. With margins at 3..10 a sixteen-character write does not
    /// run to column 16 — it wraps at the right margin and resumes at the LEFT margin on the next
    /// row, which reads as lost text in a screen dump and is not.
    /// </remarks>
    [Fact]
    public void Left_and_right_margins_clip_and_wrap()
    {
        var terminal = Sized(40, 6);

        terminal.Write($"{Esc}[2J{Esc}[?69h{Esc}[3;10s");           // DECLRMM, margins 3..10
        terminal.Write($"{Esc}[1;1HABCDEFGHIJKLMNOP");

        Assert.Equal("ABCDEFGHIJ", terminal.GetLine(0));
        Assert.Equal("  KLMNOP", terminal.GetLine(1));

        terminal.Write($"{Esc}[1;3H{Esc}[2P");                      // DCH inside the margins
        Assert.Equal("ABEFGHIJ", terminal.GetLine(0));
        Assert.Equal("  KLMNOP", terminal.GetLine(1));              // untouched beyond the margin
    }

    /// <summary>
    /// Reverse wraparound (mode 45) steps back onto a line that WRAPPED, and only then.
    /// </summary>
    /// <remarks>
    /// The condition is the whole test. Backing up from a row the cursor reached by an explicit
    /// move must not wrap, which is why a first attempt at this looked like the feature was missing.
    /// </remarks>
    [Fact]
    public void Reverse_wraparound_applies_to_a_wrapped_line()
    {
        var wrapped = Sized(6, 3);
        wrapped.Write($"{Esc}[2J{Esc}[?7h{Esc}[?45h");
        wrapped.Write("ABCDEFG");                                   // wraps after column 6
        wrapped.Write("\b\bZ");
        Assert.Equal("ABCDEZ", wrapped.GetLine(0));

        var off = Sized(6, 3);
        off.Write($"{Esc}[2J{Esc}[?7h{Esc}[?45l");
        off.Write("ABCDEFG");
        off.Write("\b\bZ");
        Assert.Equal("ABCDEF", off.GetLine(0));
        Assert.Equal("Z", off.GetLine(1));
    }

    /// <summary>IRM inserts rather than overwrites. vttest menu 8.</summary>
    [Fact]
    public void Insert_mode_shifts_instead_of_overwriting()
    {
        var replace = Sized(20, 3);
        replace.Write($"{Esc}[2J{Esc}[4l{Esc}[1;1HABCDEF{Esc}[1;3HXY");
        Assert.Equal("ABXYEF", replace.GetLine(0));

        var insert = Sized(20, 3);
        insert.Write($"{Esc}[2J{Esc}[4h{Esc}[1;1HABCDEF{Esc}[1;3HXY");
        Assert.Equal("ABXYCDEF", insert.GetLine(0));
    }

    /// <summary>HTS sets a stop that TAB lands on, after TBC has cleared the defaults.</summary>
    [Fact]
    public void A_tab_stop_can_be_set_and_landed_on()
    {
        var terminal = Sized(40, 3);

        terminal.Write($"{Esc}[2J{Esc}[3g");                        // clear all stops
        terminal.Write($"{Esc}[1;1HA{Esc}[1;10H{Esc}H");            // stop at column 10
        terminal.Write($"{Esc}[1;1H\tB");

        Assert.Equal("A        B", terminal.GetLine(0));
    }

    /// <summary>
    /// DECRQCRA's arithmetic: the checksum is the negated 16-bit sum of the characters.
    /// </summary>
    /// <remarks>
    /// Only the part that is settled. What an UNTOUCHED cell should contribute is the open question
    /// in tomlm/XTerm.NET#128 and is deliberately not asserted here — this pins the negated sum so a
    /// fix for that cannot quietly change the rest of it.
    /// </remarks>
    [Fact]
    public void Rectangular_area_checksums_negate_the_sum_of_the_characters()
    {
        var terminal = Sized(20, 5);
        var replies = new List<string>();
        terminal.DataReceived += (_, e) => replies.Add(e.Data);

        terminal.Write($"{Esc}[2;1HAB");
        terminal.Write($"{Esc}[1;1;2;1;2;2*y");                     // DECRQCRA over just "AB"

        // 0x41 + 0x42 = 0x83, negated over 16 bits is 0xFF7D.
        Assert.Equal($"{Esc}P1!~FF7D{Esc}\\", Assert.Single(replies));
    }

    /// <summary>
    /// The three ways to change the page size differ only in whether they erase.
    /// </summary>
    /// <remarks>
    /// DECCOLM erases, DECNCSM (mode 95) turns that off, and DECSCPP never erases at all -- it sets
    /// the width and says nothing about the contents. vttest's page-format tests cannot check any of
    /// this through a pty: it samples the screen size when a test starts and does not re-measure, so
    /// its "80 of 132 columns" is its own view rather than the terminal's.
    /// </remarks>
    [Fact]
    public void Page_size_controls_differ_only_in_whether_they_erase()
    {
        var terminal = Sized(80, 24);
        terminal.Write($"{Esc}[?40h");                              // Allow80To132

        // DECCOLM: resizes and erases.
        terminal.Write($"{Esc}[1;1HKEEP ME");
        terminal.Write($"{Esc}[?3h");
        Assert.Equal(132, terminal.Cols);
        Assert.Equal(string.Empty, terminal.GetLine(0));

        // DECNCSM: resizes and keeps.
        terminal.Write($"{Esc}[?95h");
        terminal.Write($"{Esc}[1;1HKEEP ME");
        terminal.Write($"{Esc}[?3l");
        Assert.Equal(80, terminal.Cols);
        Assert.Equal("KEEP ME", terminal.GetLine(0));

        // DECSCPP: resizes and keeps, whatever DECNCSM says.
        terminal.Write($"{Esc}[?95l");
        terminal.Write($"{Esc}[132$|");
        Assert.Equal(132, terminal.Cols);
        Assert.Equal("KEEP ME", terminal.GetLine(0));

        terminal.Write($"{Esc}[80$|");
        Assert.Equal(80, terminal.Cols);
    }

    /// <summary>
    /// DECSLPP sets the page length. It already worked, and is pinned because a sweep reported it
    /// as broken on the strength of vttest's own row count -- which is not evidence about this.
    /// </summary>
    [Fact]
    public void Lines_per_page_resizes_the_terminal()
    {
        var terminal = Sized(80, 24);

        terminal.Write($"{Esc}[25t");
        Assert.Equal(25, terminal.Rows);

        terminal.Write($"{Esc}[48t");
        Assert.Equal(48, terminal.Rows);
    }
}
