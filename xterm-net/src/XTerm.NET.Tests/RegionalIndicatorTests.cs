using XTerm.Options;

namespace XTerm.Tests;

/// <summary>
/// Two regional indicator symbols form one flag, and they arrive in separate <c>Print</c> calls.
///
/// <para>They used to be measured as width 0 — the pairing test lived in <c>GetStringCellWidth</c>, which is
/// called once per printed character, so the count was always 1, always odd, and the answer was always
/// zero. Width 0 leaves the cursor standing still, so the next character overwrote the indicator: a flag did
/// not render oddly, it vanished from the buffer entirely and took the column alignment of the rest of the
/// line with it.</para>
/// </summary>
public class RegionalIndicatorTests
{
    private const string RegionalU = "\U0001F1FA";
    private const string RegionalS = "\U0001F1F8";
    private const string RegionalG = "\U0001F1EC";
    private const string RegionalB = "\U0001F1E7";
    private const string FlagUs = RegionalU + RegionalS;

    private static Terminal Write(string text, int cols = 20)
    {
        var terminal = new Terminal(new TerminalOptions { Cols = cols, Rows = 5 });
        terminal.Write(text);
        return terminal;
    }

    /// <summary>The reported bug: a flag and everything after it on the line.</summary>
    [Fact]
    public void A_flag_is_one_double_width_cell_and_does_not_eat_what_follows()
    {
        var terminal = Write(FlagUs + "XXX");
        var line = terminal.Buffer.Lines[0]!;

        Assert.Equal(FlagUs, line[0].Content);
        Assert.Equal(2, line[0].Width);
        Assert.Equal(0, line[1].Width);

        // The whole point. These used to land at 0, 1, 2, on top of the flag.
        Assert.Equal("X", line[2].Content);
        Assert.Equal("X", line[3].Content);
        Assert.Equal("X", line[4].Content);
        Assert.Equal(5, terminal.Buffer.X);
    }

    /// <summary>
    /// A lone indicator is a valid character in its own right — it renders as a letter in a box — so it
    /// occupies a column rather than being erased by whatever comes next.
    /// </summary>
    [Fact]
    public void A_lone_indicator_keeps_its_column()
    {
        var terminal = Write(RegionalU + "X");
        var line = terminal.Buffer.Lines[0]!;

        Assert.Equal(RegionalU, line[0].Content);
        Assert.Equal(1, line[0].Width);
        Assert.Equal("X", line[1].Content);
        Assert.Equal(2, terminal.Buffer.X);
    }

    /// <summary>
    /// Indicators pair from the left and do not accumulate, so a third starts a new pair rather than
    /// joining the flag beside it.
    /// </summary>
    [Fact]
    public void A_third_indicator_starts_a_new_pair()
    {
        var terminal = Write(RegionalU + RegionalS + RegionalG);
        var line = terminal.Buffer.Lines[0]!;

        Assert.Equal(FlagUs, line[0].Content);
        Assert.Equal(2, line[0].Width);
        Assert.Equal(RegionalG, line[2].Content);
        Assert.Equal(1, line[2].Width);
        Assert.Equal(3, terminal.Buffer.X);
    }

    /// <summary>And a fourth completes that second pair.</summary>
    [Fact]
    public void Four_indicators_are_two_flags()
    {
        var terminal = Write(RegionalG + RegionalB + RegionalU + RegionalS);
        var line = terminal.Buffer.Lines[0]!;

        Assert.Equal(RegionalG + RegionalB, line[0].Content);
        Assert.Equal(2, line[0].Width);
        Assert.Equal(FlagUs, line[2].Content);
        Assert.Equal(2, line[2].Width);
        Assert.Equal(4, terminal.Buffer.X);
    }

    /// <summary>
    /// The halves arriving in separate writes is the normal case, not the exotic one: a pty hands over
    /// whatever the read returned, so the boundary falls mid-flag whenever it happens to.
    /// </summary>
    [Fact]
    public void The_halves_pair_across_separate_writes()
    {
        var terminal = new Terminal(new TerminalOptions { Cols = 20, Rows = 5 });
        terminal.Write(RegionalU);
        terminal.Write(RegionalS);

        var line = terminal.Buffer.Lines[0]!;
        Assert.Equal(FlagUs, line[0].Content);
        Assert.Equal(2, line[0].Width);
        Assert.Equal(2, terminal.Buffer.X);
    }

    /// <summary>
    /// Anything that moves the cursor between them leaves two separate characters. They are only a flag
    /// because they were adjacent, and a cursor address means they are not.
    /// </summary>
    [Fact]
    public void Indicators_separated_by_a_cursor_move_do_not_pair()
    {
        var terminal = new Terminal(new TerminalOptions { Cols = 20, Rows = 5 });
        terminal.Write(RegionalU);
        terminal.Write("[1;10H");     // somewhere else entirely
        terminal.Write(RegionalS);

        var line = terminal.Buffer.Lines[0]!;
        Assert.Equal(RegionalU, line[0].Content);
        Assert.Equal(1, line[0].Width);
        Assert.Equal(RegionalS, line[9].Content);
        Assert.Equal(1, line[9].Width);
    }

    /// <summary>Nor across a newline, for the same reason.</summary>
    [Fact]
    public void Indicators_separated_by_a_newline_do_not_pair()
    {
        var terminal = new Terminal(new TerminalOptions { Cols = 20, Rows = 5 });
        terminal.Write(RegionalU);
        terminal.Write("\r\n");
        terminal.Write(RegionalS);

        Assert.Equal(RegionalU, terminal.Buffer.Lines[0]![0].Content);
        Assert.Equal(RegionalS, terminal.Buffer.Lines[1]![0].Content);
    }

    /// <summary>
    /// A pair that cannot fit stays two characters rather than becoming a wide cell hanging off the edge.
    /// Two boxed letters at the margin is a better answer than a flag half off the screen.
    /// </summary>
    [Fact]
    public void A_pair_that_will_not_fit_stays_two_characters()
    {
        // Four columns, three taken, so the second indicator lands in the last one.
        var terminal = Write("abc" + RegionalU + RegionalS, cols: 4);
        var line = terminal.Buffer.Lines[0]!;

        Assert.Equal(RegionalU, line[3].Content);
        Assert.Equal(1, line[3].Width);
    }

    /// <summary>
    /// U+FFFC has the same shape of bug and the same consequence: it was measured 0, so it never moved the
    /// cursor and the next character printed over it.
    /// </summary>
    /// <remarks>
    /// It shares a branch with ZWJ, which subtracts the width of the glyph in front of it. With nothing in
    /// front, that subtracted from zero. Found by sweeping every codepoint ucs-detect expects to be narrow
    /// against what the buffer actually does — it was the only one of 36,254 that moved the cursor wrongly.
    /// </remarks>
    [Fact]
    public void A_lone_object_replacement_character_keeps_its_column()
    {
        var terminal = Write("￼X");
        var line = terminal.Buffer.Lines[0]!;

        Assert.Equal("￼", line[0].Content);
        Assert.Equal(1, line[0].Width);
        Assert.Equal("X", line[1].Content);
        Assert.Equal(2, terminal.Buffer.X);
    }

    /// <summary>
    /// The other clusters keep working. Included because this change touches the shared width routine, and
    /// the ZWJ and skin-tone paths run through the same method.
    /// </summary>
    [Theory]
    [InlineData("\U0001F468‍\U0001F469‍\U0001F467‍\U0001F466")]   // family, ZWJ
    [InlineData("\U0001F9D1\U0001F3FD")]                                        // person, skin tone
    [InlineData("❤️‍\U0001F525")]                                // heart on fire
    public void Other_clusters_are_still_one_double_width_cell(string cluster)
    {
        var terminal = Write(cluster + "X");
        var line = terminal.Buffer.Lines[0]!;

        Assert.Equal(cluster, line[0].Content);
        Assert.Equal(2, line[0].Width);
        Assert.Equal("X", line[2].Content);
    }
}
