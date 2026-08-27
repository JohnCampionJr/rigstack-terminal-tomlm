using System.Text;

namespace XTerm.Bench;

/// <summary>
/// Writes the byte streams the benchmarks replay.
///
/// Vendored verbatim from Avalloy.Terminal.Bench so the emulator numbers here are directly
/// comparable with the end-to-end numbers recorded there. Same fixed seed, same streams.
///
/// These are GENERATED rather than captured, because a baseline is only useful if a later run can be
/// compared against it, and that requires the input to be identical every time. Every stream here is
/// produced from a fixed seed, so the corpus on your machine is the corpus in CI.
///
/// Note that <c>target</c> is a CHARACTER count, not a byte count — checking bytes would mean
/// re-encoding the whole accumulated buffer on every iteration. The byte length each stream actually
/// reached is measured once, after encoding, and that is the figure the results report.
///
/// Each stream isolates a different cost in the renderer, which is the point: a single "terminal
/// benchmark" number hides which of them regressed.
/// </summary>
public static class CorpusGenerator
{
    /// <summary>Streams the harness knows how to generate, and what each one is actually probing.</summary>
    public static readonly IReadOnlyList<CorpusSpec> Specs =
    [
        new("scroll-ascii",
            "Plain ASCII lines, one style throughout — the `cat bigfile` case.",
            "Baseline scroll cost. One styled run per line, so this is close to the floor: whatever "
            + "this costs, nothing else can be cheaper."),

        new("sgr-churn",
            "16-colour SGR changes every few cells — build logs, `ls --color`, grep output.",
            "Run-splitting cost. Every colour change ends the current run and starts another, so the "
            + "per-run overhead that scroll-ascii amortises over a whole line is paid dozens of times."),

        new("truecolor",
            "A 24-bit gradient, a distinct colour per cell.",
            "The pathological end of run-splitting: every single cell is its own run. If per-run cost "
            + "dominates, this is where it becomes impossible to hide."),

        new("alt-redraw",
            "Alternate-buffer full-screen repaints with direct cursor addressing — vim, htop, less.",
            "Redraw rather than scroll. No new lines are produced, so scrollback and reflow are out of "
            + "the picture and what remains is the cost of repainting a screen that mostly did not change."),

        new("unicode",
            "CJK wide glyphs, ZWJ emoji sequences, and combining marks.",
            "Shaping and width resolution — the paths that cannot use a fixed-advance fast path. "
            + "Expect this to be the slowest stream by a wide margin."),

        new("flood",
            "`yes`-style unthrottled short lines.",
            "Throughput under backpressure: far more output arrives than can ever be painted, which is "
            + "what exposes whether the emulator or the renderer is the limiting stage."),
    ];

    public static CorpusSpec SpecFor(string name) =>
        Specs.FirstOrDefault(s => s.Name == name)
        ?? throw new ArgumentException(
            $"Unknown corpus '{name}'. Known: {string.Join(", ", Specs.Select(s => s.Name))}");

    /// <summary>
    /// Generates every stream into <paramref name="dir"/>, skipping any that already exists at the
    /// requested size — regenerating is pointless work and, worse, invites a silent change of input
    /// under a baseline that was recorded against the old bytes.
    /// </summary>
    public static IReadOnlyList<CorpusFile> GenerateAll(string dir, int targetBytes, int cols, int rows)
    {
        Directory.CreateDirectory(dir);
        var files = new List<CorpusFile>();

        foreach (var spec in Specs)
        {
            var path = Path.Combine(dir, spec.Name + ".vt");
            if (!File.Exists(path) || new FileInfo(path).Length < targetBytes)
            {
                var bytes = Generate(spec.Name, targetBytes, cols, rows);
                File.WriteAllBytes(path, bytes);
            }
            files.Add(new CorpusFile(spec, path, new FileInfo(path).Length));
        }

        return files;
    }

    public static byte[] Generate(string name, int targetBytes, int cols, int rows) => name switch
    {
        "scroll-ascii" => ScrollAscii(targetBytes, cols),
        "sgr-churn" => SgrChurn(targetBytes, cols),
        "truecolor" => TrueColor(targetBytes, cols),
        "alt-redraw" => AltRedraw(targetBytes, cols, rows),
        "unicode" => Unicode(targetBytes, cols),
        "flood" => Flood(targetBytes),
        _ => throw new ArgumentException($"Unknown corpus '{name}'"),
    };

    // A fixed seed is the whole contract of this file. Do not make it a parameter.
    private const int Seed = 0x5EED;

    private static byte[] ScrollAscii(int target, int cols)
    {
        var rng = new Random(Seed);
        var sb = new StringBuilder(target + 4096);
        const string words = "the quick brown fox jumps over a lazy dog while parsing escape sequences ";

        while (sb.Length < target)
        {
            var width = rng.Next(cols / 3, cols);
            var start = rng.Next(words.Length);
            for (var i = 0; i < width; i++)
                sb.Append(words[(start + i) % words.Length]);
            sb.Append("\r\n");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] SgrChurn(int target, int cols)
    {
        var rng = new Random(Seed);
        var sb = new StringBuilder(target + 4096);
        const string words = "warn error info debug trace fatal ok skip pass fail ";

        while (sb.Length < target)
        {
            var written = 0;
            while (written < cols - 12)
            {
                sb.Append("\u001b[").Append(30 + rng.Next(8)).Append(';').Append(rng.Next(2) == 0 ? "1" : "22").Append('m');
                var start = rng.Next(words.Length);
                var len = rng.Next(3, 11);
                for (var i = 0; i < len; i++)
                    sb.Append(words[(start + i) % words.Length]);
                written += len;
            }
            sb.Append("\u001b[0m\r\n");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] TrueColor(int target, int cols)
    {
        var sb = new StringBuilder(target + 4096);
        var row = 0;

        while (sb.Length < target)
        {
            for (var x = 0; x < cols; x++)
            {
                // A moving hue ramp, so consecutive rows never repeat a cached run.
                var r = (x * 255 / Math.Max(1, cols - 1) + row) & 0xFF;
                var g = (255 - x * 255 / Math.Max(1, cols - 1) + row * 3) & 0xFF;
                var b = (row * 7) & 0xFF;
                sb.Append("\u001b[38;2;").Append(r).Append(';').Append(g).Append(';').Append(b).Append('m').Append('█');
            }
            sb.Append("\u001b[0m\r\n");
            row++;
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] AltRedraw(int target, int cols, int rows)
    {
        var sb = new StringBuilder(target + 4096);
        sb.Append("\u001b[?1049h");   // enter alternate buffer

        var frame = 0;
        while (sb.Length < target)
        {
            sb.Append("\u001b[H");    // home, then repaint every row in place
            for (var y = 1; y <= rows; y++)
            {
                sb.Append("\u001b[").Append(y).Append(";1H");
                sb.Append("\u001b[").Append(30 + (y + frame) % 8).Append('m');
                var bar = (y * 3 + frame) % Math.Max(1, cols - 20);
                sb.Append('│').Append(new string('━', bar)).Append(new string(' ', Math.Max(0, cols - bar - 2)));
            }
            sb.Append("\u001b[0m");
            frame++;
        }

        sb.Append("\u001b[?1049l");   // leave it as we found it
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] Unicode(int target, int cols)
    {
        var rng = new Random(Seed);
        var sb = new StringBuilder(target + 4096);

        string[] pieces =
        [
            "世界",                       // CJK, two cells each
            "こんにちは",                 // kana
            "👨‍👩‍👧‍👦", // ZWJ family — one grapheme, many codepoints
            "🇯🇵",                        // regional indicator pair
            "éàô",      // combining marks
            "한국어",                      // hangul
            "ascii-run",                  // so the fast path is represented too
        ];

        while (sb.Length < target)
        {
            var written = 0;
            while (written < cols - 10)
            {
                var piece = pieces[rng.Next(pieces.Length)];
                sb.Append(piece).Append(' ');
                written += piece.Length + 1;
            }
            sb.Append("\r\n");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] Flood(int target)
    {
        var sb = new StringBuilder(target + 64);
        while (sb.Length < target)
            sb.Append("y\r\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}

public sealed record CorpusSpec(string Name, string What, string Probes);

public sealed record CorpusFile(CorpusSpec Spec, string Path, long Bytes);
