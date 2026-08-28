using XTerm.Options;
using Xunit;

namespace XTerm.Tests;

/// <summary>
/// DECSLRM -- left and right margins, and the DECLRMM mode (69) that turns them on.
///
/// <para>The margins themselves are the easy half. What makes the feature real is that every
/// operation which moves content honours them: wrapping, scrolling, IL/DL and ICH/DCH. A terminal
/// that reports the mode as supported and then scrolls the whole screen anyway is worse than one
/// that reports nothing, because an application asks before it relies on this.</para>
/// </summary>
public class LeftRightMarginTests
{
    private const string Esc = "\u001b";

    private static Terminal Fresh(int cols = 20, int rows = 6)
        => new(new TerminalOptions { Cols = cols, Rows = rows });

    /// <summary>A terminal with margins already set, stated 1-based as an application would.</summary>
    private static Terminal WithMargins(int left = 4, int right = 9, int cols = 20, int rows = 6)
    {
        var t = Fresh(cols, rows);
        t.Write($"{Esc}[?69h{Esc}[{left};{right}s");
        return t;
    }

    private static string Row(Terminal terminal, int row = 0)
    {
        var line = terminal.Buffer.Lines[terminal.Buffer.YBase + row]!;
        return string.Concat(Enumerable.Range(0, terminal.Cols).Select(c => line[c].Content))
                     .TrimEnd('\0', ' ');
    }

    // ---- the mode, and the sequence it unlocks -------------------------------------------------

    /// <summary>
    /// CSI s is Save Cursor until DECLRMM says otherwise. Getting this backwards would make an
    /// application's margins silently save the cursor, or a save silently set margins.
    /// </summary>
    [Fact]
    public void Without_the_mode_CSI_s_still_saves_the_cursor()
    {
        var terminal = Fresh();

        terminal.Write($"{Esc}[3;5H{Esc}[3;9s{Esc}[1;1H{Esc}[u");

        Assert.Equal(0, terminal.Buffer.ScrollLeft);
        Assert.Equal(terminal.Cols - 1, terminal.Buffer.ScrollRight);
        Assert.Equal(4, terminal.Buffer.X);
        Assert.Equal(2, terminal.Buffer.Y);
    }

    [Fact]
    public void With_the_mode_CSI_s_sets_the_margins()
    {
        var terminal = WithMargins(left: 4, right: 9);

        Assert.Equal(3, terminal.Buffer.ScrollLeft);
        Assert.Equal(8, terminal.Buffer.ScrollRight);
    }

    /// <summary>Setting margins homes the cursor, as DECSTBM does.</summary>
    [Fact]
    public void Setting_margins_homes_the_cursor()
    {
        var terminal = Fresh();
        terminal.Write($"{Esc}[4;7H{Esc}[?69h{Esc}[4;9s");

        Assert.Equal(0, terminal.Buffer.X);
        Assert.Equal(0, terminal.Buffer.Y);
    }

    /// <summary>Omitted parameters mean the extremes, so a bare CSI s under the mode widens out.</summary>
    [Fact]
    public void A_bare_sequence_widens_the_margins_again()
    {
        var terminal = WithMargins();

        terminal.Write($"{Esc}[s");

        Assert.Equal(0, terminal.Buffer.ScrollLeft);
        Assert.Equal(terminal.Cols - 1, terminal.Buffer.ScrollRight);
    }

    /// <summary>
    /// A right margin at or left of the left one is refused rather than clamped: the old margins
    /// stay, which the application can at least query, instead of a region it did not ask for.
    /// </summary>
    [Fact]
    public void A_degenerate_pair_is_refused_and_leaves_the_old_margins()
    {
        var terminal = WithMargins(left: 4, right: 9);

        terminal.Write($"{Esc}[9;4s");

        Assert.Equal(3, terminal.Buffer.ScrollLeft);
        Assert.Equal(8, terminal.Buffer.ScrollRight);
    }

    [Fact]
    public void Turning_the_mode_off_widens_the_margins()
    {
        var terminal = WithMargins();

        terminal.Write($"{Esc}[?69l");

        Assert.Equal(0, terminal.Buffer.ScrollLeft);
        Assert.Equal(terminal.Cols - 1, terminal.Buffer.ScrollRight);
    }

    /// <summary>
    /// Without a way to ask, a well-behaved application never uses the feature -- so DECRQM has to
    /// answer for this mode, not only for the ones that came before it.
    /// </summary>
    [Fact]
    public void The_mode_can_be_queried()
    {
        var terminal = Fresh();
        var replies = new List<string>();
        terminal.DataReceived += (_, e) => replies.Add(e.Data);

        terminal.Write($"{Esc}[?69$p");
        terminal.Write($"{Esc}[?69h{Esc}[?69$p");

        Assert.Equal(new[] { $"{Esc}[?69;2$y", $"{Esc}[?69;1$y" }, replies);
    }

    // ---- the operations that make it real -------------------------------------------------------

    [Fact]
    public void Text_wraps_at_the_right_margin_and_resumes_at_the_left()
    {
        var terminal = WithMargins(left: 4, right: 9);   // columns 3..8, six wide

        // Into the margins first. DECSLRM homes the cursor to column 1 of the SCREEN, not of the
        // region, unless origin mode is on -- and a cursor outside the margins is not in the region,
        // so it wraps at the screen edge like any other text. That is xterm's rule, and it is what
        // stops a status line drawn outside a pane being folded into it.
        terminal.Write($"{Esc}[1;4H");
        terminal.Write("abcdefghi");

        Assert.Equal("   abcdef", Row(terminal, 0));
        Assert.Equal("   ghi", Row(terminal, 1));
    }

    /// <summary>
    /// The batched writer bypasses the per-character wrap check, so it has to be bounded by the
    /// margin itself. Without that it writes straight through and out the other side -- and only
    /// when the fast path takes the write, which reads as an intermittent fault rather than a
    /// missing case.
    /// </summary>
    [Fact]
    public void The_batched_and_per_character_paths_agree_about_the_margin()
    {
        var batched = WithMargins(left: 4, right: 9);
        batched.Write($"{Esc}[1;4H");
        batched.Write("abcdefghijkl");

        var perCharacter = WithMargins(left: 4, right: 9);
        perCharacter.UseRunPrinting = false;
        perCharacter.Write($"{Esc}[1;4H");
        perCharacter.Write("abcdefghijkl");

        Assert.Equal(Row(perCharacter, 0), Row(batched, 0));
        Assert.Equal(Row(perCharacter, 1), Row(batched, 1));
        Assert.Equal("   abcdef", Row(batched, 0));
    }

    /// <summary>And the same through the byte entry, which is a third writer again.</summary>
    [Fact]
    public void The_byte_entry_agrees_about_the_margin()
    {
        var terminal = WithMargins(left: 4, right: 9);

        terminal.Write(System.Text.Encoding.UTF8.GetBytes($"{Esc}[1;4Habcdefghi"));

        Assert.Equal("   abcdef", Row(terminal, 0));
        Assert.Equal("   ghi", Row(terminal, 1));
    }

    /// <summary>
    /// The case the feature exists for: scrolling one pane of a side-by-side layout must leave the
    /// other pane alone. This is what a whole-line scroll gets wrong.
    /// </summary>
    [Fact]
    public void Scrolling_inside_the_margins_leaves_the_columns_outside_untouched()
    {
        var terminal = Fresh(cols: 12, rows: 4);

        terminal.Write("LLLmmmmmmRRR");
        terminal.Write($"{Esc}[2;1HLLLnnnnnnRRR");

        terminal.Write($"{Esc}[?69h{Esc}[4;9s");
        terminal.Write($"{Esc}[1;4H");
        terminal.Write($"{Esc}[S");

        Assert.Equal("LLLnnnnnnRRR", Row(terminal, 0));
        Assert.Equal("LLL      RRR", Row(terminal, 1).PadRight(12));
    }

    [Fact]
    public void Inserting_a_line_shifts_only_the_margin_columns()
    {
        var terminal = Fresh(cols: 12, rows: 4);

        terminal.Write("LLLmmmmmmRRR");
        terminal.Write($"{Esc}[?69h{Esc}[4;9s");
        terminal.Write($"{Esc}[1;4H{Esc}[L");

        Assert.Equal("LLL      RRR", Row(terminal, 0).PadRight(12));
        Assert.Equal("   mmmmmm", Row(terminal, 1));
    }

    /// <summary>
    /// From outside the margin columns there is no region to shift, so IL does nothing. A cursor in
    /// the right-hand pane shifting the left pane's lines is the corruption margins prevent.
    /// </summary>
    [Fact]
    public void Inserting_a_line_from_outside_the_margins_does_nothing()
    {
        var terminal = Fresh(cols: 12, rows: 4);

        terminal.Write("LLLmmmmmmRRR");
        terminal.Write($"{Esc}[?69h{Esc}[4;9s");
        terminal.Write($"{Esc}[1;11H{Esc}[L");

        Assert.Equal("LLLmmmmmmRRR", Row(terminal, 0));
    }

    [Fact]
    public void Inserting_characters_stops_at_the_right_margin()
    {
        var terminal = Fresh(cols: 12, rows: 4);

        terminal.Write("LLLmmmmmmRRR");
        terminal.Write($"{Esc}[?69h{Esc}[4;9s");
        terminal.Write($"{Esc}[1;4H{Esc}[2@");

        Assert.Equal("LLL  mmmmRRR", Row(terminal, 0));
    }

    [Fact]
    public void Deleting_characters_pulls_in_from_inside_the_margin_only()
    {
        var terminal = Fresh(cols: 12, rows: 4);

        terminal.Write("LLLmmmmmmRRR");
        terminal.Write($"{Esc}[?69h{Esc}[4;9s");
        terminal.Write($"{Esc}[1;4H{Esc}[2P");

        Assert.Equal("LLLmmmm  RRR", Row(terminal, 0));
    }

    /// <summary>Under origin mode the region is a box, so column 1 is the left margin.</summary>
    [Fact]
    public void Origin_mode_addresses_columns_from_the_left_margin()
    {
        var terminal = WithMargins(left: 4, right: 9);

        terminal.Write($"{Esc}[?6h{Esc}[1;1Hx");

        Assert.Equal("   x", Row(terminal, 0));
    }

    // ---- and what has to survive ---------------------------------------------------------------

    [Fact]
    public void A_resize_clamps_the_margins()
    {
        var terminal = WithMargins(left: 4, right: 15, cols: 20);

        terminal.Resize(8, terminal.Rows);

        Assert.Equal(3, terminal.Buffer.ScrollLeft);
        Assert.Equal(7, terminal.Buffer.ScrollRight);
    }

    /// <summary>
    /// A resize that would leave the region degenerate widens it instead, rather than leaving a
    /// region no write could land in.
    /// </summary>
    [Fact]
    public void A_resize_past_the_left_margin_widens_them_again()
    {
        var terminal = WithMargins(left: 10, right: 15, cols: 20);

        terminal.Resize(4, terminal.Rows);

        Assert.Equal(0, terminal.Buffer.ScrollLeft);
        Assert.Equal(3, terminal.Buffer.ScrollRight);
    }

    [Fact]
    public void A_full_reset_widens_the_margins_and_clears_the_mode()
    {
        var terminal = WithMargins();

        terminal.Write($"{Esc}c");

        Assert.Equal(0, terminal.Buffer.ScrollLeft);
        Assert.Equal(terminal.Cols - 1, terminal.Buffer.ScrollRight);
        Assert.False(terminal.LeftRightMarginMode);
    }

    /// <summary>
    /// With the mode off, nothing changes anywhere. This is the regression that matters most, since
    /// margins are off for every application that has never heard of them.
    /// </summary>
    [Fact]
    public void With_no_margins_set_everything_behaves_as_before()
    {
        var terminal = Fresh(cols: 8, rows: 3);

        terminal.Write("abcdefghij");

        Assert.Equal("abcdefgh", Row(terminal, 0));
        Assert.Equal("ij", Row(terminal, 1));
    }
}
