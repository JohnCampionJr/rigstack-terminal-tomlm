using BenchmarkDotNet.Attributes;
using XTerm.Options;

namespace XTerm.Bench;

/// <summary>
/// Steady-state emulation cost, per corpus stream.
///
/// MemoryDiagnoser is the point of this file, not a decoration. The hypothesis under test is that
/// XTerm.NET's throughput is dominated by per-character heap allocation rather than by parsing work,
/// and "allocated bytes per operation" measures that directly — divide by the character count and you
/// get bytes allocated per printed character, which either is or is not near zero. A time-only
/// benchmark could not tell the two explanations apart.
///
/// ShortRun keeps a full sweep to a couple of minutes. The absolute numbers move slightly versus a
/// full run; the ratios that matter here do not.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5, invocationCount: 1)]
public class ParseBenchmarks
{
    private readonly Dictionary<string, string[]> _chunks = new();
    private readonly Dictionary<string, int> _charCounts = new();

    /// <summary>
    /// The grid a real 1080p terminal gets. Cols matter: they decide when a line wraps and therefore
    /// how much scrolling a stream provokes.
    /// </summary>
    private const int Cols = 240;
    private const int Rows = 67;

    /// <summary>4 KiB is what a PTY read actually returns; one giant Write would amortise work no real session amortises.</summary>
    private const int ChunkChars = 4096;

    [Params("scroll-ascii", "sgr-churn", "truecolor", "alt-redraw", "unicode", "flood")]
    public string Corpus { get; set; } = "scroll-ascii";

    private Terminal _terminal = null!;

    [GlobalSetup]
    public void Setup()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "corpus");
        CorpusGenerator.GenerateAll(dir, targetBytes: 400_000, cols: Cols, rows: Rows);

        foreach (var spec in CorpusGenerator.Specs)
        {
            var text = File.ReadAllText(Path.Combine(dir, spec.Name + ".vt"));
            var list = new List<string>();
            for (var i = 0; i < text.Length; i += ChunkChars)
                list.Add(text.Substring(i, Math.Min(ChunkChars, text.Length - i)));
            _chunks[spec.Name] = list.ToArray();
            _charCounts[spec.Name] = text.Length;
        }
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // A fresh terminal per iteration keeps scrollback state from drifting across measurements.
        // The JIT warm-up that a fresh terminal does NOT reset is handled by BenchmarkDotNet's own
        // warmup iterations, which is exactly what it is for.
        _terminal = new Terminal(new TerminalOptions { Cols = Cols, Rows = Rows });
    }

    [Benchmark]
    public void Parse()
    {
        var chunks = _chunks[Corpus];
        foreach (var c in chunks)
            _terminal.Write(c);
    }

    /// <summary>Characters fed per operation, so a report can express results per character.</summary>
    public int CharsPerOp => _charCounts[Corpus];
}
