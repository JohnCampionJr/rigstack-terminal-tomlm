using XTerm.Options;

namespace XTerm.Tests;

/// <summary>
/// DECRQCRA — the checksum report esctest builds every content assertion on. A terminal without
/// it cannot be conformance-tested at all, which is why it leads the esctest campaign.
/// </summary>
public class ChecksumReportTests
{
    private const string Esc = "";

    private static Terminal NewTerminal(int cols = 20, int rows = 5) =>
        new(new TerminalOptions { Cols = cols, Rows = rows });

    private static string? Reply(Terminal terminal, string sequence)
    {
        string? reply = null;
        terminal.DataReceived += (_, e) => reply = e.Data;
        terminal.Write(sequence);
        return reply;
    }

    private static int Sum(string text)
    {
        var sum = 0;
        foreach (var ch in text) sum += ch;
        return sum;
    }

    /// <summary>The negated-sum convention xterm used before patch #279, which esctest's default expects.</summary>
    private static string Report(int id, int sum) => $"{Esc}P{id}!~{(0x10000 - sum) & 0xFFFF:X4}{Esc}\\";

    [Fact]
    public void A_single_cells_checksum_is_its_character()
    {
        var terminal = NewTerminal();
        terminal.Write("Hello");

        Assert.Equal(Report(1, 'H'), Reply(terminal, $"{Esc}[1;0;1;1;1;1*y"));
    }

    [Fact]
    public void A_rects_checksum_is_the_sum_of_its_characters()
    {
        var terminal = NewTerminal();
        terminal.Write("Hello");

        Assert.Equal(Report(7, Sum("Hello")), Reply(terminal, $"{Esc}[7;0;1;1;1;5*y"));
    }

    [Fact]
    public void A_cell_nothing_ever_wrote_counts_as_a_space()
    {
        // Erased and never-written alike: DEC terminals trim trailing blanks and esctest's client
        // side reasons that away, but only if blanks come back as spaces rather than zeros.
        var terminal = NewTerminal();

        Assert.Equal(Report(2, 3 * 0x20), Reply(terminal, $"{Esc}[2;0;2;1;2;3*y"));
    }

    [Fact]
    public void The_trailing_half_of_a_wide_character_adds_nothing()
    {
        // The wide character was already counted in full one cell to the left; counting its
        // placeholder too would double it, and counting it as a blank would add a phantom space.
        var terminal = NewTerminal();
        terminal.Write("世");

        Assert.Equal(Report(3, '世'), Reply(terminal, $"{Esc}[3;0;1;1;1;2*y"));
    }

    [Fact]
    public void Coordinates_are_clamped_to_the_screen()
    {
        var terminal = NewTerminal(cols: 10, rows: 3);
        terminal.Write("AB");

        // A rect hanging off every edge still answers, for what the screen actually holds.
        Assert.Equal(Report(4, Sum("AB") + (3 * 10 - 2) * 0x20),
                     Reply(terminal, $"{Esc}[4;0;1;1;99;99*y"));
    }

    [Fact]
    public void Omitted_coordinates_mean_the_whole_screen()
    {
        var terminal = NewTerminal(cols: 4, rows: 2);
        terminal.Write("hi");

        Assert.Equal(Report(5, Sum("hi") + 6 * 0x20), Reply(terminal, $"{Esc}[5*y"));
    }

    [Fact]
    public void Attributes_contribute_nothing_to_the_checksum()
    {
        // esctest compares a cell's checksum to the bare codepoint of the character it expects;
        // a weight per attribute bit would fail every assertion on styled text.
        var terminal = NewTerminal();
        terminal.Write($"{Esc}[1;4;7;31mX");

        Assert.Equal(Report(6, 'X'), Reply(terminal, $"{Esc}[6;0;1;1;1;1*y"));
    }
}
