using XTerm;
using XTerm.Common;
using XTerm.Options;

namespace XTerm.Tests;

/// <summary>
/// Covers OSC 4 (indexed palette), OSC 10/11/12 (foreground, background, cursor), and the
/// OSC 104/110/111/112 resets.
/// </summary>
public class ColorPaletteTests
{
    private Terminal CreateTerminal(ThemeOptions? theme = null)
    {
        var options = new TerminalOptions { Cols = 80, Rows = 24 };
        if (theme is not null)
        {
            options.Theme = theme;
        }

        return new Terminal(options);
    }

    private static List<string> CaptureReplies(Terminal terminal)
    {
        var replies = new List<string>();
        terminal.DataReceived += (_, e) => replies.Add(e.Data);
        return replies;
    }

    // ---- defaults ----------------------------------------------------------------------------

    [Fact]
    public void Defaults_MatchXtermAnsiColors()
    {
        var terminal = CreateTerminal();

        Assert.Equal(0x000000, terminal.Colors[0]);
        Assert.Equal(0xCD0000, terminal.Colors[1]);
        Assert.Equal(0xFFFFFF, terminal.Colors[15]);
    }

    [Fact]
    public void Defaults_ComputeThe216ColorCube()
    {
        var terminal = CreateTerminal();

        Assert.Equal(0x000000, terminal.Colors[16]);   // cube origin
        Assert.Equal(0xFFFFFF, terminal.Colors[231]);  // cube far corner
        Assert.Equal(0x005FAF, terminal.Colors[25]);   // r=0 g=1 b=3 -> 0,95,175
    }

    [Fact]
    public void Defaults_ComputeTheGrayscaleRamp()
    {
        var terminal = CreateTerminal();

        Assert.Equal(0x080808, terminal.Colors[232]);
        Assert.Equal(0xEEEEEE, terminal.Colors[255]);
    }

    [Fact]
    public void Defaults_PreserveThePreviousQueryAnswers_WhenNoThemeIsSet()
    {
        // This change must not move colours for an embedder that sets no theme; it only stops the
        // answers being constants.
        var terminal = CreateTerminal();

        Assert.Equal(0xFFFFFF, terminal.Colors.Foreground);
        Assert.Equal(0x000000, terminal.Colors.Background);
        Assert.Equal(0xFFFFFF, terminal.Colors.Cursor);
    }

    // ---- the light background case ------------------------------------------------------------

    [Fact]
    public void Theme_SetsTheBackground_AndTheQueryReportsIt()
    {
        // The reason this PR exists. A program asks what the background is before choosing its own
        // colours; a constant reply of black made every one of them render for a dark terminal.
        var terminal = CreateTerminal(new ThemeOptions { Background = "#ffffff", Foreground = "#000000" });
        var replies = CaptureReplies(terminal);

        terminal.Write("\x1B]11;?\x07");

        Assert.Equal("\u001b]11;rgb:ffff/ffff/ffff\u0007", Assert.Single(replies));
    }

    [Fact]
    public void IsLightBackground_FollowsTheTheme()
    {
        Assert.True(CreateTerminal(new ThemeOptions { Background = "#ffffff" }).Colors.IsLightBackground);
        Assert.False(CreateTerminal(new ThemeOptions { Background = "#000000" }).Colors.IsLightBackground);

        // Luma-weighted rather than averaged: pure blue is dark despite a high channel value.
        Assert.False(CreateTerminal(new ThemeOptions { Background = "#0000ff" }).Colors.IsLightBackground);
    }

    [Fact]
    public void ApplyTheme_ReseedsAtRuntime_ForAnOsThemeFlip()
    {
        var terminal = CreateTerminal(new ThemeOptions { Background = "#000000" });
        Assert.False(terminal.Colors.IsLightBackground);

        terminal.Colors.ApplyTheme(new ThemeOptions { Background = "#ffffff", Foreground = "#000000" });

        Assert.True(terminal.Colors.IsLightBackground);
        Assert.Equal(0x000000, terminal.Colors.Foreground);
    }

    [Fact]
    public void Reset_RestoresTheEmbedderTheme_NotAFactoryDarkPalette()
    {
        // The failure this guards: a program sets colours, then resets, and a light terminal is
        // left black because "reset" meant xterm's defaults rather than the configured theme.
        var terminal = CreateTerminal(new ThemeOptions { Background = "#ffffff", Black = "#eeeeee" });

        terminal.Write("\x1B]11;rgb:00/00/00\x07");
        terminal.Write("\x1B]4;0;#123456\x07");
        Assert.Equal(0x000000, terminal.Colors.Background);

        terminal.Write("\x1B]111\x07");
        terminal.Write("\x1B]104\x07");

        Assert.Equal(0xFFFFFF, terminal.Colors.Background);
        Assert.Equal(0xEEEEEE, terminal.Colors[0]);
    }

    [Fact]
    public void Theme_OverridesAnsiSlots()
    {
        var terminal = CreateTerminal(new ThemeOptions { Red = "#ff8800", BrightWhite = "rgb:11/22/33" });

        Assert.Equal(0xFF8800, terminal.Colors[1]);
        Assert.Equal(0x112233, terminal.Colors[15]);
    }

    // ---- OSC 4 -------------------------------------------------------------------------------

    [Fact]
    public void Osc4_SetsAnIndexedColor()
    {
        var terminal = CreateTerminal();

        terminal.Write("\x1B]4;1;rgb:ff/00/00\x07");

        Assert.Equal(0xFF0000, terminal.Colors[1]);
    }

    [Fact]
    public void Osc4_SetsMultiplePairsInOneSequence()
    {
        // Theme scripts send all sixteen at once rather than as sixteen sequences.
        var terminal = CreateTerminal();

        terminal.Write("\x1B]4;1;#ff0000;2;#00ff00;3;#0000ff\x07");

        Assert.Equal(0xFF0000, terminal.Colors[1]);
        Assert.Equal(0x00FF00, terminal.Colors[2]);
        Assert.Equal(0x0000FF, terminal.Colors[3]);
    }

    [Fact]
    public void Osc4_QueriesAnIndexedColor()
    {
        var terminal = CreateTerminal();
        var replies = CaptureReplies(terminal);

        terminal.Write("\x1B]4;1;?\x07");

        Assert.Equal("\u001b]4;1;rgb:cdcd/0000/0000\u0007", Assert.Single(replies));
    }

    [Fact]
    public void Osc4_QueryReflectsAPriorSet()
    {
        var terminal = CreateTerminal();
        terminal.Write("\x1B]4;5;#010203\x07");
        var replies = CaptureReplies(terminal);

        terminal.Write("\x1B]4;5;?\x07");

        Assert.Equal("\u001b]4;5;rgb:0101/0202/0303\u0007", Assert.Single(replies));
    }

    [Fact]
    public void Osc4_IgnoresOutOfRangeAndMalformedEntries()
    {
        var terminal = CreateTerminal();

        terminal.Write("\x1B]4;999;#ffffff\x07");
        terminal.Write("\x1B]4;1;notacolor\x07");

        Assert.Equal(0xCD0000, terminal.Colors[1]);
    }

    [Fact]
    public void Osc104_ResetsASingleIndex()
    {
        var terminal = CreateTerminal();
        terminal.Write("\x1B]4;1;#ffffff\x07");
        terminal.Write("\x1B]4;2;#ffffff\x07");

        terminal.Write("\x1B]104;1\x07");

        Assert.Equal(0xCD0000, terminal.Colors[1]);
        Assert.Equal(0xFFFFFF, terminal.Colors[2]);
    }

    // ---- OSC 10/11/12 ------------------------------------------------------------------------

    [Fact]
    public void Osc10_SetsForeground()
    {
        var terminal = CreateTerminal();

        terminal.Write("\x1B]10;#abcdef\x07");

        Assert.Equal(0xABCDEF, terminal.Colors.Foreground);
    }

    [Fact]
    public void Osc10_WithMultipleSpecs_AdvancesThroughResources()
    {
        // xterm defines OSC 10 ; fg ; bg as setting both; handling only the first drops the
        // background silently.
        var terminal = CreateTerminal();

        terminal.Write("\x1B]10;#111111;#222222;#333333\x07");

        Assert.Equal(0x111111, terminal.Colors.Foreground);
        Assert.Equal(0x222222, terminal.Colors.Background);
        Assert.Equal(0x333333, terminal.Colors.Cursor);
    }

    [Fact]
    public void Osc12_SetsCursor()
    {
        var terminal = CreateTerminal();

        terminal.Write("\x1B]12;red\x07");

        Assert.Equal(0xFF0000, terminal.Colors.Cursor);
    }

    [Theory]
    [InlineData("110", 0xFFFFFF)]
    [InlineData("111", 0x000000)]
    [InlineData("112", 0xFFFFFF)]
    public void Osc110To112_ResetTheirOwnResource(string code, int expected)
    {
        var terminal = CreateTerminal();
        terminal.Write("\x1B]10;#123456\x07");
        terminal.Write("\x1B]11;#123456\x07");
        terminal.Write("\x1B]12;#123456\x07");

        terminal.Write($"\x1B]{code}\x07");

        var actual = code switch
        {
            "110" => terminal.Colors.Foreground,
            "111" => terminal.Colors.Background,
            _ => terminal.Colors.Cursor,
        };
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ColorChanged_FiresForSetsAndNotForNoOps()
    {
        var terminal = CreateTerminal();
        var changes = new List<ColorChangedEventArgs>();
        terminal.Colors.ColorChanged += (_, e) => changes.Add(e);

        terminal.Write("\x1B]4;1;#ff0000\x07");
        terminal.Write("\x1B]4;1;#ff0000\x07");   // same value, no repaint needed

        var change = Assert.Single(changes);
        Assert.Equal(ColorTarget.Indexed, change.Target);
        Assert.Equal(1, change.Index);
        Assert.Equal(0xFF0000, change.Rgb);
    }

    // ---- colour spec parsing -----------------------------------------------------------------

    [Theory]
    [InlineData("rgb:ff/00/00", 0xFF0000)]
    [InlineData("rgb:f/0/0", 0xFF0000)]              // 1 digit: f is FULL intensity, not 0x0f
    [InlineData("rgb:ffff/0000/0000", 0xFF0000)]     // 4 digits, as emitted by queries
    [InlineData("#ff0000", 0xFF0000)]
    [InlineData("#f00", 0xFF0000)]
    [InlineData("#ffff00000000", 0xFF0000)]
    [InlineData("red", 0xFF0000)]
    [InlineData("RED", 0xFF0000)]
    public void ColorSpec_ParsesTheFormsProgramsActuallySend(string spec, int expected)
    {
        Assert.True(ColorSpec.TryParse(spec, out var rgb));
        Assert.Equal(expected, rgb);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("rgb:ff/00")]
    [InlineData("rgb:gg/00/00")]
    [InlineData("#ff00")]
    [InlineData("chartreuse")]
    public void ColorSpec_RejectsWhatItCannotRead(string? spec)
    {
        Assert.False(ColorSpec.TryParse(spec, out _));
    }

    [Fact]
    public void ColorSpec_FormatWidensChannelsByRepetition()
    {
        // 0xff must become 0xffff, not 0xff00: full intensity has to survive the widening.
        Assert.Equal("rgb:ffff/0000/8080", ColorSpec.Format(0xFF0080));
    }

    [Fact]
    public void ColorSpec_RoundTripsThroughFormatAndParse()
    {
        Assert.True(ColorSpec.TryParse(ColorSpec.Format(0x123456), out var rgb));
        Assert.Equal(0x123456, rgb);
    }
}
