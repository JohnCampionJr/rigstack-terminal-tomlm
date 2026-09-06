using System.Diagnostics;
using System.Text.Json;
using XTerm;
using XTerm.Options;

namespace XTerm.Bench;

/// <summary>
/// One measured run of every corpus, as JSON, for a CI job to compare against another run.
///
/// <para>Fixed WORK rather than fixed time, unlike <c>alloc</c>. A time-boxed loop measures a
/// different amount of work on every machine, which makes two runs incomparable on anything but the
/// derived rates — and the rate is the noisy part. A fixed pass count means the allocation total is
/// the same measurement on both sides, and the run takes a bounded, predictable time.</para>
///
/// <para>This mode deliberately touches only <see cref="Terminal"/>, <see cref="TerminalOptions"/>
/// and <c>Write(string)</c>. That is what lets a CI job run THIS harness against an OLDER build of
/// the library by dropping its assembly in: anything newer would fail at run time, and then the
/// comparison could only ever be same-version.</para>
/// </summary>
public static class CiProbe
{
    private const int Cols = 240;
    private const int Rows = 67;

    /// <summary>
    /// Roughly what each corpus costs per character, relative to <c>unicode</c>.
    /// </summary>
    /// <remarks>
    /// <para>The budget is divided by these, so every corpus is measured for about the same LENGTH
    /// OF TIME rather than over the same number of characters. Equal characters sounds fairer and is
    /// not: <c>flood</c> costs about 28x what <c>scroll-ascii</c> does, so an equal-character budget
    /// measures the fast corpora for a twenty-eighth as long and hands them all the noise. Observed
    /// on a GitHub runner, <c>scroll-ascii</c> came back with a ±16% spread against ±1-4% for
    /// everything else, which put its gate at 49% -- no gate at all.</para>
    /// <para>Constants, and deliberately not measured at run time: both sides of a comparison must
    /// do identical work, and a figure derived from a warm-up would differ between them. Being wrong
    /// only makes the run uneven, never incorrect -- every number is reported per character.</para>
    /// </remarks>
    private static readonly Dictionary<string, double> RelativeCost = new()
    {
        ["scroll-ascii"] = 0.11,
        ["sgr-churn"] = 0.30,
        ["truecolor"] = 0.32,
        ["alt-redraw"] = 0.40,
        ["unicode"] = 1.00,
        ["flood"] = 2.96,
    };

    public static int Run(string outputPath, long targetChars, long warmChars)
    {
        var results = new List<CorpusResult>();

        foreach (var spec in CorpusGenerator.Specs)
            results.Add(Measure(spec.Name, targetChars, warmChars));

        var report = new Report
        {
            Runtime = Environment.Version.ToString(),
            Library = LibraryVersion(),
            TargetChars = targetChars,
            Corpora = results
        };

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(outputPath, json);

        Console.WriteLine($"{"corpus",-14} {"ns/char",9} {"bytes/char",11} {"gen0/Mchar",11}");
        Console.WriteLine(new string('-', 50));
        foreach (var r in results)
            Console.WriteLine($"{r.Name,-14} {r.NsPerChar,9:N2} {r.BytesPerChar,11:N2} {r.Gen0PerMchar,11:N2}");
        Console.WriteLine();
        Console.WriteLine($"library under test: {report.Library}");
        Console.WriteLine($"written to {outputPath}");
        return 0;
    }

    private static CorpusResult Measure(string corpus, long targetChars, long warmChars)
    {
        var (chunks, chars) = Load(corpus);
        var terminal = new Terminal(new TerminalOptions { Cols = Cols, Rows = Rows });

        // Passes come from a time budget divided by the corpus's known relative cost, so each is
        // measured for about as long as the others. Still fixed work: the corpus is generated from a
        // fixed seed and the cost is a constant, so both sides of a comparison run exactly the same
        // number of passes over exactly the same bytes.
        var cost = RelativeCost.TryGetValue(corpus, out var known) ? known : 1.0;
        var passes = (int)Math.Max(1, targetChars / cost / Math.Max(1, chars));
        var warmup = (int)Math.Max(1, warmChars / cost / Math.Max(1, chars));

        // Warm to let tiered compilation promote the hot methods. Measuring before that measures the
        // JIT, which is how warming for a fixed count rather than to convergence produced a number
        // four times off earlier in this project's history.
        for (var i = 0; i < warmup; i++)
            foreach (var c in chunks) terminal.Write(c);

        // Collect first, so nothing from the warm-up is counted against the measured passes.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var beforeAlloc = GC.GetTotalAllocatedBytes(precise: true);
        var beforeGen0 = GC.CollectionCount(0);
        var sw = Stopwatch.StartNew();

        for (var i = 0; i < passes; i++)
            foreach (var c in chunks) terminal.Write(c);

        sw.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - beforeAlloc;
        var gen0 = GC.CollectionCount(0) - beforeGen0;

        long charsDone = (long)chars * passes;

        return new CorpusResult
        {
            Name = corpus,
            Chars = charsDone,
            AllocatedBytes = allocated,
            Gen0 = gen0,
            BytesPerChar = (double)allocated / charsDone,
            Gen0PerMchar = gen0 / (charsDone / 1_000_000.0),
            NsPerChar = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / charsDone,
            MibPerSec = charsDone / 1024.0 / 1024.0 / sw.Elapsed.TotalSeconds,
            Passes = passes
        };
    }

    private static (string[] Chunks, int Chars) Load(string corpus)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "corpus");
        CorpusGenerator.GenerateAll(dir, targetBytes: 400_000, cols: Cols, rows: Rows);

        var text = File.ReadAllText(Path.Combine(dir, corpus + ".vt"));
        var chunks = new List<string>();
        for (var i = 0; i < text.Length; i += 4096)
            chunks.Add(text.Substring(i, Math.Min(4096, text.Length - i)));
        return (chunks.ToArray(), text.Length);
    }

    /// <summary>
    /// Which XTerm.NET actually got loaded, identified by its module version id.
    /// </summary>
    /// <remarks>
    /// The MVID rather than the path or the version, because a CI job compares two builds by
    /// swapping the assembly into one output directory -- so the path is identical by design and the
    /// version usually is too. The MVID is regenerated by every compilation, so it is the one field
    /// that actually answers "are these two different builds". A comparison that measured the same
    /// build twice would otherwise report a flawless result and mean nothing at all.
    /// </remarks>
    private static string LibraryVersion()
    {
        var asm = typeof(Terminal).Assembly;
        var name = asm.GetName();
        return $"{name.Name} {name.Version} mvid:{asm.ManifestModule.ModuleVersionId}";
    }
}

public sealed class Report
{
    public string Runtime { get; set; } = "";
    public string Library { get; set; } = "";
    public long TargetChars { get; set; }
    public List<CorpusResult> Corpora { get; set; } = new();
}

public sealed class CorpusResult
{
    public string Name { get; set; } = "";
    public int Passes { get; set; }
    public long Chars { get; set; }
    public long AllocatedBytes { get; set; }
    public int Gen0 { get; set; }
    public double BytesPerChar { get; set; }
    public double Gen0PerMchar { get; set; }
    public double NsPerChar { get; set; }
    public double MibPerSec { get; set; }
}
