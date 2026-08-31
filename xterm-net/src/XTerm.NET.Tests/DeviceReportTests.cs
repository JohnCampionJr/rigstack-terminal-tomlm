using XTerm;
using XTerm.Options;

namespace XTerm.Tests;

/// <summary>
/// The canned status reports: the DSR family beyond CPR, DECID, and the level-aware DA replies.
/// Expected strings are what xterm sends, which is what esctest grades against.
/// </summary>
public class DeviceReportTests
{
    private const string Esc = "\u001b";

    private static (Terminal terminal, List<string> replies) Create()
    {
        var terminal = new Terminal(new TerminalOptions { Cols = 80, Rows = 24 });
        var replies = new List<string>();
        terminal.DataReceived += (_, e) => replies.Add(e.Data);
        return (terminal, replies);
    }

    [Theory]
    [InlineData("[?53n", "[?53n")]              // DEC locator status: available
    [InlineData("[?55n", "[?53n")]              // xterm's locator status alias
    [InlineData("[?56n", "[?57;1n")]            // locator type: mouse
    [InlineData("[?62n", "[0*{")]               // DECMSR: no macro space (unprefixed reply)
    [InlineData("[?63;123n", "P123!~0000\u001b\\")] // DECCKSR echoes the id; no macros, zero sum
    [InlineData("[?75n", "[?70n")]              // data integrity: no errors
    [InlineData("[?85n", "[?83n")]              // multiple sessions: not configured
    public void CannedDsrReports_AnswerLikeXterm(string query, string reply)
    {
        var (terminal, replies) = Create();
        terminal.Write(Esc + query);
        Assert.Equal(Esc + reply, Assert.Single(replies));
    }

    [Fact]
    public void Decid_AnswersWithThePrimaryDeviceAttributes()
    {
        var (terminal, replies) = Create();
        terminal.Write(Esc + "Z");
        terminal.Write(Esc + "[c");
        Assert.Equal(2, replies.Count);
        Assert.Equal(replies[1], replies[0]);
        Assert.StartsWith(Esc + "[?65;", replies[0]);
    }

    [Fact]
    public void PrimaryDa_FollowsTheConformanceLevelDown()
    {
        var (terminal, replies) = Create();
        terminal.Write(Esc + "[63;1\"p");        // DECSCL: drop to level 3
        terminal.Write(Esc + "[c");
        Assert.StartsWith(Esc + "[?63;", Assert.Single(replies));
        Assert.DoesNotContain(";28", replies[0]); // no rectangular-editing claim below level 4
    }

    [Fact]
    public void SecondaryDa_ReportsTheVt520FamilyAndPatchLevel()
    {
        var (terminal, replies) = Create();
        terminal.Write(Esc + "[>c");
        Assert.Equal(Esc + "[>64;383;0c", Assert.Single(replies));
    }

    [Fact]
    public void Decxcpr_ReportsThePageAndHonoursOriginMode()
    {
        var (terminal, replies) = Create();
        terminal.Write(Esc + "[?6l");            // origin mode off
        terminal.Write(Esc + "[3;7H");
        terminal.Write(Esc + "[?6n");
        Assert.Equal(Esc + "[?3;7;1R", replies[^1]);

        terminal.Write(Esc + "[5;20r" + Esc + "[?6h" + Esc + "[2;4H");
        terminal.Write(Esc + "[?6n");
        Assert.Equal(Esc + "[?2;4;1R", replies[^1]);
    }
}
