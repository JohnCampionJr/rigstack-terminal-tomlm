using XTerm.Input;
using XTerm.Options;

namespace XTerm.Tests;

/// <summary>
/// The terminal side of the Kitty keyboard protocol: the four CSI u sequences, the per-screen
/// flag stacks, and the query an application actually probes with.
/// </summary>
public class KittyKeyboardStateTests
{
    private static Terminal NewTerminal(bool enabled = true)
        => new(new TerminalOptions { Cols = 20, Rows = 5, KittyKeyboardEnabled = enabled });

    private static KittyKeyboardFlags Flags(Terminal t) => t.KittyKeyboardState.Flags;

    // ----- CSI = flags ; mode u ------------------------------------------------------------

    [Fact]
    public void Set_assigns_the_flags()
    {
        var t = NewTerminal();
        t.Write("\u001b[=5u");
        Assert.Equal(
            KittyKeyboardFlags.DisambiguateEscapeCodes | KittyKeyboardFlags.ReportAlternateKeys,
            Flags(t));
    }

    [Fact]
    public void Set_mode_two_only_sets_the_given_bits()
    {
        var t = NewTerminal();
        t.Write("\u001b[=1u");
        t.Write("\u001b[=2;2u");
        Assert.Equal(
            KittyKeyboardFlags.DisambiguateEscapeCodes | KittyKeyboardFlags.ReportEventTypes,
            Flags(t));
    }

    [Fact]
    public void Set_mode_three_only_clears_the_given_bits()
    {
        var t = NewTerminal();
        t.Write("\u001b[=3u");
        t.Write("\u001b[=1;3u");
        Assert.Equal(KittyKeyboardFlags.ReportEventTypes, Flags(t));
    }

    [Fact]
    public void Set_with_no_parameters_clears_the_flags()
    {
        var t = NewTerminal();
        t.Write("\u001b[=31u");
        t.Write("\u001b[=u");
        Assert.Equal(KittyKeyboardFlags.None, Flags(t));
    }

    // ----- CSI ? u -------------------------------------------------------------------------

    [Fact]
    public void Query_answers_with_the_active_flags()
    {
        var t = NewTerminal();
        var responses = new List<string>();
        t.DataReceived += (_, e) => responses.Add(e.Data);

        t.Write("\u001b[?u");
        t.Write("\u001b[=5u");
        t.Write("\u001b[?u");

        Assert.Equal(new[] { "\u001b[?0u", "\u001b[?5u" }, responses);
    }

    [Fact]
    public void Query_does_not_move_the_cursor()
    {
        // The regression that motivated the exact-match lookup: the identifier "?u" used to be
        // stripped to "u" and executed RESTORE CURSOR, so Neovim's startup probe for Kitty
        // support teleported the cursor to wherever CSI s last saved it.
        var t = NewTerminal();
        t.Write("\u001b[2;2H\u001b[s");   // save at (1,1)...
        t.Write("\u001b[4;6H");            // ...move away
        t.Write("\u001b[?u");

        Assert.Equal(5, t.Buffer.X);
        Assert.Equal(3, t.Buffer.Y);
    }

    [Fact]
    public void Bare_CSI_u_still_restores_the_cursor()
    {
        var t = NewTerminal();
        t.Write("\u001b[2;2H\u001b[s");
        t.Write("\u001b[4;6H");
        t.Write("\u001b[u");

        Assert.Equal(1, t.Buffer.X);
        Assert.Equal(1, t.Buffer.Y);
    }

    // ----- CSI > flags u / CSI < count u ---------------------------------------------------

    [Fact]
    public void Push_saves_the_current_flags_and_pop_restores_them()
    {
        var t = NewTerminal();
        t.Write("\u001b[=1u");
        t.Write("\u001b[>2u");
        t.Write("\u001b[>15u");
        Assert.Equal((KittyKeyboardFlags)15, Flags(t));

        t.Write("\u001b[<u");
        Assert.Equal(KittyKeyboardFlags.ReportEventTypes, Flags(t));

        // Spec: "If a pop request is received that empties the stack, all flags are reset" — so
        // the LAST pop yields zero, not the value it popped. The base state set with CSI = is
        // not stack-tracked; draining the stack is a return to protocol-off.
        t.Write("\u001b[<u");
        Assert.Equal(KittyKeyboardFlags.None, Flags(t));
    }

    [Fact]
    public void Pop_takes_a_count()
    {
        var t = NewTerminal();
        t.Write("\u001b[=1u");
        t.Write("\u001b[>2u");
        t.Write("\u001b[>4u");
        t.Write("\u001b[>8u");
        t.Write("\u001b[<2u");
        Assert.Equal((KittyKeyboardFlags)2, Flags(t));
    }

    [Fact]
    public void Popping_past_the_bottom_leaves_the_flags_at_zero()
    {
        var t = NewTerminal();
        t.Write("\u001b[=1u");
        t.Write("\u001b[<5u");
        Assert.Equal(KittyKeyboardFlags.None, Flags(t));
    }

    [Fact]
    public void The_stack_is_bounded_by_evicting_the_oldest_entry()
    {
        // An application looping on push must not grow memory forever: spec says a full stack
        // evicts its OLDEST entry. Twenty pushes therefore leave sixteen entries, not twenty —
        // fifteen pops still find values, and the sixteenth empties the stack (which resets the
        // flags to zero, per the pop rule above).
        var t = NewTerminal();
        t.Write("\u001b[=1u");
        for (var i = 0; i < 20; i++)
            t.Write("\u001b[>8u");

        for (var i = 0; i < 15; i++)
            t.Write("\u001b[<u");
        Assert.Equal((KittyKeyboardFlags)8, Flags(t));

        t.Write("\u001b[<u");
        Assert.Equal(KittyKeyboardFlags.None, Flags(t));
    }

    // ----- Per-screen flags ----------------------------------------------------------------

    [Fact]
    public void The_alternate_screen_has_its_own_flags()
    {
        var t = NewTerminal();
        t.Write("\u001b[=1u");           // the shell's flags, on the main screen
        t.Write("\u001b[?1049h");        // a full-screen app starts...
        Assert.Equal(KittyKeyboardFlags.None, Flags(t));

        t.Write("\u001b[=8u");           // ...and sets its own
        t.Write("\u001b[?1049l");        // and exits
        Assert.Equal(KittyKeyboardFlags.DisambiguateEscapeCodes, Flags(t));

        // Its flags are still waiting if it comes back.
        t.Write("\u001b[?1049h");
        Assert.Equal(KittyKeyboardFlags.ReportAllKeysAsEscapeCodes, Flags(t));
    }

    [Fact]
    public void An_application_dying_without_popping_cannot_poison_the_shell()
    {
        // The scenario the per-screen rule exists for: vim pushes flags on the alternate screen
        // and crashes. Leaving the alternate screen must hand the shell ITS flags back.
        var t = NewTerminal();
        t.Write("\u001b[?1049h");
        t.Write("\u001b[>31u");          // vim pushes everything on
        t.Write("\u001b[?1049l");        // crash: no pop
        Assert.Equal(KittyKeyboardFlags.None, Flags(t));
    }

    [Fact]
    public void Each_screen_pops_from_its_own_stack()
    {
        var t = NewTerminal();
        t.Write("\u001b[=1u");
        t.Write("\u001b[>2u");           // main stack: [1]
        t.Write("\u001b[>4u");           // main stack: [1, 2], flags 4
        t.Write("\u001b[?1049h");
        t.Write("\u001b[<u");            // alt stack is empty: flags stay 0
        Assert.Equal(KittyKeyboardFlags.None, Flags(t));

        t.Write("\u001b[?1049l");
        Assert.Equal((KittyKeyboardFlags)4, Flags(t));
        t.Write("\u001b[<u");            // the alt-screen pop consumed NOTHING of main's stack
        Assert.Equal(KittyKeyboardFlags.ReportEventTypes, Flags(t));
    }

    // ----- Reset ---------------------------------------------------------------------------

    [Fact]
    public void RIS_clears_the_flags_and_both_stacks()
    {
        // RIS is exactly how someone recovers from an application that set flags and died.
        var t = NewTerminal();
        t.Write("\u001b[=31u");
        t.Write("\u001b[>31u");
        t.Write("\u001bc");
        Assert.Equal(KittyKeyboardFlags.None, Flags(t));

        t.Write("\u001b[<u");            // the stack is gone too, not just the flags
        Assert.Equal(KittyKeyboardFlags.None, Flags(t));
    }

    // ----- The option gate -----------------------------------------------------------------

    [Fact]
    public void When_disabled_the_sequences_are_consumed_in_silence()
    {
        var t = NewTerminal(enabled: false);
        var responses = new List<string>();
        t.DataReceived += (_, e) => responses.Add(e.Data);

        t.Write("\u001b[=31u");
        t.Write("\u001b[>31u");
        t.Write("\u001b[?u");            // no answer is how a terminal says "legacy encoding"

        Assert.Equal(KittyKeyboardFlags.None, Flags(t));
        Assert.Empty(responses);
        Assert.False(t.KittyKeyboardActive);
    }

    [Fact]
    public void When_disabled_the_query_still_does_not_move_the_cursor()
    {
        var t = NewTerminal(enabled: false);
        t.Write("\u001b[2;2H\u001b[s");
        t.Write("\u001b[4;6H");
        t.Write("\u001b[?u");

        Assert.Equal(5, t.Buffer.X);
        Assert.Equal(3, t.Buffer.Y);
    }

    // ----- The terminal-level API the host uses --------------------------------------------

    [Fact]
    public void KittyKeyboardActive_follows_the_flags()
    {
        var t = NewTerminal();
        Assert.False(t.KittyKeyboardActive);
        t.Write("\u001b[=1u");
        Assert.True(t.KittyKeyboardActive);
        t.Write("\u001b[=u");
        Assert.False(t.KittyKeyboardActive);
    }

    [Fact]
    public void GenerateKittyKeyInput_encodes_under_the_active_flags()
    {
        var t = NewTerminal();
        t.Write("\u001b[=1u");
        var ev = new KeyEvent { Key = "Escape" };
        Assert.Equal("\u001b[27u", t.GenerateKittyKeyInput(ev));
    }

    [Fact]
    public void GenerateKittyKeyInput_honours_MacOptionIsMeta()
    {
        var t = new Terminal(new TerminalOptions { Cols = 20, Rows = 5, MacOptionIsMeta = true });
        t.Write("\u001b[=1u");
        var ev = new KeyEvent { Key = "ƒ", Code = "KeyF", AltKey = true };
        Assert.Equal("\u001b[102;3u", t.GenerateKittyKeyInput(ev));
    }
}
