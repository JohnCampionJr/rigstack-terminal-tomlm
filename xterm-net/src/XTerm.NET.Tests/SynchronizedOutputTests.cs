using XTerm;
using XTerm.Options;

namespace XTerm.Tests;

/// <summary>
/// DEC private mode 2026 — synchronized output.
/// </summary>
/// <remarks>
/// The emulator's whole job here is to report the state honestly and answer when asked whether it
/// understands the mode at all. Holding the frame is the renderer's decision, and so is the timeout
/// that has to bound it.
/// </remarks>
public class SynchronizedOutputTests
{
    private const string Esc = "\u001b";

    private static Terminal Fresh() => new(new TerminalOptions { Cols = 20, Rows = 5 });

    [Fact]
    public void Begins_and_ends_with_the_mode()
    {
        var terminal = Fresh();
        Assert.False(terminal.SynchronizedOutput);

        terminal.Write($"{Esc}[?2026h");
        Assert.True(terminal.SynchronizedOutput);

        terminal.Write($"{Esc}[?2026l");
        Assert.False(terminal.SynchronizedOutput);
    }

    /// <summary>
    /// A renderer needs to know the moment an update starts and ends, not to discover it on its next
    /// frame — so the change is an event, not just a property.
    /// </summary>
    [Fact]
    public void Raises_an_event_at_each_edge()
    {
        var terminal = Fresh();
        var edges = new List<bool>();
        terminal.SynchronizedOutputChanged += (_, active) => edges.Add(active);

        terminal.Write($"{Esc}[?2026h");
        terminal.Write($"{Esc}[?2026l");

        Assert.Equal(new[] { true, false }, edges);
    }

    /// <summary>
    /// Applications wrap each frame, so the same mode is set over and over. Only real transitions
    /// should reach a renderer — an event per frame that says nothing changed is noise it would have
    /// to filter itself.
    /// </summary>
    [Fact]
    public void Repeating_the_same_state_raises_nothing()
    {
        var terminal = Fresh();
        var edges = 0;
        terminal.SynchronizedOutputChanged += (_, _) => edges++;

        terminal.Write($"{Esc}[?2026h");
        terminal.Write($"{Esc}[?2026h");
        terminal.Write($"{Esc}[?2026h");

        Assert.Equal(1, edges);
    }

    /// <summary>
    /// The content written inside an update is ordinary content — the mode changes nothing about how
    /// it is parsed or stored, only when a renderer is willing to show it.
    /// </summary>
    [Fact]
    public void Content_written_inside_an_update_lands_normally()
    {
        var terminal = Fresh();

        terminal.Write($"{Esc}[?2026h");
        terminal.Write("hello");
        terminal.Write($"{Esc}[?2026l");

        Assert.Equal("hello", terminal.Buffer.Lines[0]!.TranslateToString(true).TrimEnd());
    }

    // ---- DECRQM, which is how an application discovers the mode exists --------------------------

    [Fact]
    public void Reports_the_mode_as_reset_when_idle()
    {
        var terminal = Fresh();
        var replies = new List<string>();
        terminal.DataReceived += (_, e) => replies.Add(e.Data);

        terminal.Write($"{Esc}[?2026$p");

        Assert.Equal(new[] { $"{Esc}[?2026;2$y" }, replies);
    }

    [Fact]
    public void Reports_the_mode_as_set_during_an_update()
    {
        var terminal = Fresh();
        var replies = new List<string>();
        terminal.DataReceived += (_, e) => replies.Add(e.Data);

        terminal.Write($"{Esc}[?2026h");
        terminal.Write($"{Esc}[?2026$p");

        Assert.Equal(new[] { $"{Esc}[?2026;1$y" }, replies);
    }

    /// <summary>
    /// Silence for every other mode, which is what this terminal did before. Answering "not
    /// recognised" for a mode that IS supported would talk an application out of a feature that
    /// works, and mode state lives in individual properties rather than a registry — so a complete
    /// answer means a switch over every mode, and a wrong entry is worse than no reply.
    /// </summary>
    [Fact]
    public void Says_nothing_about_modes_it_cannot_answer_for()
    {
        var terminal = Fresh();
        var replies = new List<string>();
        terminal.DataReceived += (_, e) => replies.Add(e.Data);

        terminal.Write($"{Esc}[?1049$p");
        terminal.Write($"{Esc}[?2004$p");
        terminal.Write($"{Esc}[?25$p");

        Assert.Empty(replies);
    }
}
