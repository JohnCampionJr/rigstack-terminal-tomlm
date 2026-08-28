using XTerm.Buffer;
using XTerm.Options;
using Xunit;

namespace XTerm.Tests;

/// <summary>
/// OSC 8 hyperlinks anchored to the columns they cover.
///
/// <para>The point of OSC 8 is a link whose DISPLAY TEXT is not the URL — "click here", a filename,
/// a commit subject. That is exactly what a regular expression over the visible text cannot find,
/// so the two are complementary rather than the same feature, and only this one can answer "what is
/// under the pointer".</para>
/// </summary>
public class HyperlinkAnchorTests
{
    private const string Esc = "\u001b";
    private const string St = Esc + "\\";

    private static string Link(string url, string parameters = "") => $"{Esc}]8;{parameters};{url}{St}";
    private static string EndLink() => Link("");

    private static Terminal Fresh(int cols = 20, int rows = 5)
        => new(new TerminalOptions { Cols = cols, Rows = rows });

    private static BufferLine Row(Terminal t, int row = 0) => t.Buffer.Lines[t.Buffer.YBase + row]!;

    [Fact]
    public void A_link_covers_the_text_printed_inside_it()
    {
        var t = Fresh();
        t.Write(Link("https://example.com") + "click here" + EndLink());

        Assert.True(Row(t).TryGetLinkAt(3, out var link));
        Assert.Equal("https://example.com", link.Url);
        Assert.Equal(0, link.Column);
        Assert.Equal(10, link.Cols);
    }

    /// <summary>The whole reason this cannot be a regex: no URL appears on screen at all.</summary>
    [Fact]
    public void The_display_text_need_not_look_like_a_url()
    {
        var t = Fresh();
        t.Write(Link("https://example.com/deep/path") + "click here" + EndLink());

        var text = string.Concat(Enumerable.Range(0, 10).Select(c => Row(t)[c].Content));
        Assert.Equal("click here", text);
        Assert.DoesNotContain("http", text);
        Assert.True(Row(t).TryGetLinkAt(0, out _));
    }

    [Fact]
    public void Text_outside_the_link_is_not_covered()
    {
        var t = Fresh();
        t.Write("before " + Link("https://example.com") + "link" + EndLink() + " after");

        Assert.False(Row(t).TryGetLinkAt(0, out _), "text before the link");
        Assert.True(Row(t).TryGetLinkAt(7, out _), "the link itself");
        Assert.False(Row(t).TryGetLinkAt(11, out _), "text after the link");
    }

    /// <summary>One span, not one per character.</summary>
    [Fact]
    public void A_contiguous_link_is_a_single_span()
    {
        var t = Fresh();
        t.Write(Link("https://example.com") + "abcdefgh" + EndLink());

        Assert.Single(Row(t).Links);
        Assert.Equal(8, Row(t).Links[0].Cols);
    }

    /// <summary>
    /// The id groups spans that are not contiguous, so a link that wrapped is one link.
    /// </summary>
    [Fact]
    public void The_id_parameter_is_kept()
    {
        var t = Fresh();
        t.Write(Link("https://example.com", "id=42") + "abc" + EndLink());

        Assert.True(Row(t).TryGetLinkAt(1, out var link));
        Assert.Equal("42", link.Id);
    }

    /// <summary>Two different links side by side stay two spans.</summary>
    [Fact]
    public void Adjacent_links_to_different_urls_are_not_joined()
    {
        var t = Fresh();
        t.Write(Link("https://a.example") + "aa" + EndLink()
              + Link("https://b.example") + "bb" + EndLink());

        Assert.Equal(2, Row(t).Links.Count);
        Assert.True(Row(t).TryGetLinkAt(0, out var first));
        Assert.True(Row(t).TryGetLinkAt(2, out var second));
        Assert.Equal("https://a.example", first.Url);
        Assert.Equal("https://b.example", second.Url);
    }

    [Fact]
    public void Typing_over_a_link_takes_those_columns_out_of_it()
    {
        var t = Fresh();
        t.Write(Link("https://example.com") + "abcdefgh" + EndLink());
        t.Write($"{Esc}[1;1HXX");

        Assert.False(Row(t).TryGetLinkAt(0, out _), "the overwritten columns are not the link");
        Assert.True(Row(t).TryGetLinkAt(2, out var rest));
        Assert.Equal(2, rest.Column);
        Assert.Equal(6, rest.Cols);
    }

    /// <summary>Writing through the middle leaves the two halves.</summary>
    [Fact]
    public void Typing_through_the_middle_splits_it()
    {
        var t = Fresh();
        t.Write(Link("https://example.com") + "abcdefgh" + EndLink());
        t.Write($"{Esc}[1;4HXX");

        Assert.Equal(2, Row(t).Links.Count);
        Assert.True(Row(t).TryGetLinkAt(0, out var left));
        Assert.True(Row(t).TryGetLinkAt(5, out var right));
        Assert.Equal(3, left.Cols);
        Assert.Equal(3, right.Cols);
        Assert.False(Row(t).TryGetLinkAt(3, out _));
    }

    /// <summary>
    /// The batched writer bypasses Print, so it keeps the bookkeeping itself. Without that a link
    /// would cover the text or not depending on which writer happened to take it.
    /// </summary>
    [Fact]
    public void The_batched_and_per_character_paths_agree()
    {
        const string input = "before ";
        var batched = Fresh();
        batched.Write(input + Link("https://example.com") + "click here" + EndLink() + " after");

        var perCharacter = Fresh();
        perCharacter.UseRunPrinting = false;
        perCharacter.Write(input + Link("https://example.com") + "click here" + EndLink() + " after");

        Assert.Equal(Describe(perCharacter), Describe(batched));
        Assert.Equal("https://example.com@7+10", Describe(batched));
    }

    /// <summary>And the byte entry, which is a third writer again.</summary>
    [Fact]
    public void The_byte_entry_agrees_too()
    {
        var viaString = Fresh();
        viaString.Write(Link("https://example.com") + "click here" + EndLink());

        var viaBytes = Fresh();
        viaBytes.Write(System.Text.Encoding.UTF8.GetBytes(
            Link("https://example.com") + "click here" + EndLink()));

        Assert.Equal(Describe(viaString), Describe(viaBytes));
    }

    /// <summary>A recycled line is a new line: the ring hands back the object it is about to drop.</summary>
    [Fact]
    public void A_line_reused_by_the_ring_carries_no_links_over()
    {
        var t = new Terminal(new TerminalOptions { Cols = 20, Rows = 3, Scrollback = 2 });
        t.Write(Link("https://example.com") + "link" + EndLink() + "\r\n");

        for (var i = 0; i < 20; i++)
            t.Write($"line {i}\r\n");

        for (var i = 0; i < t.Buffer.Lines.Length; i++)
            Assert.False(t.Buffer.Lines[i]?.HasLinks ?? false,
                         $"row {i} kept a link from a line the ring had dropped");
    }

    /// <summary>Ordinary output carries no links, and pays nothing to say so.</summary>
    [Fact]
    public void Text_with_no_link_records_none()
    {
        var t = Fresh();
        t.Write("just some ordinary output");

        Assert.False(Row(t).HasLinks);
        Assert.Empty(Row(t).Links);
    }

    private static string Describe(Terminal t)
        => string.Join(" ", Row(t).Links.Select(l => $"{l.Url}@{l.Column}+{l.Cols}"));
}
