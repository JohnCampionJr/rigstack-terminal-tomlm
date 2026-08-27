using System.Diagnostics;
using System.Text;
using XTerm;
using XTerm.Buffer;
using XTerm.Options;

namespace Cat150;

/// <summary>
/// Ghostty's throughput test, run against this stack.
///
/// Ghostty reports catting a 150 MB file through a real terminal: 1.5 s for 1.3.2 and 575 ms for
/// nightly, against 1.2 s for Alacritty and 3.8 s for Warp. Those are END-TO-END numbers — the
/// terminal parses AND draws. Every measurement in this repo so far has been emulation with no
/// renderer attached, which is why it could not honestly be set against them.
///
/// This closes as much of that gap as can be closed without a GPU: same file size, fed in PTY-sized
/// reads, with a renderer walking the viewport on a frame cadence.
///
/// WHAT IT IS NOT: the renderer here groups cells into styled runs and materialises their text, which
/// is the CPU half of what a real one does. It never shapes a glyph, uploads a texture or presents a
/// frame. These are a LOWER BOUND on end-to-end cost and are NOT a like-for-like result against
/// Ghostty's. What they are good for is the comparison this file exists to make: identical harness,
/// identical input, identical renderer, one library swapped underneath.
///
/// The same source compiles against the released XTerm.NET and against the fork, which is the only
/// way to compare them — they share an assembly name and cannot coexist in one process.
/// </summary>
public static class Harness
{
    private const int Cols = 240;
    private const int Rows = 67;

    /// <summary>What a PTY read actually hands back.</summary>
    private const int ReadSize = 4096;

    /// <summary>60 fps. A terminal coalesces output between frames rather than drawing every write.</summary>
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(1000.0 / 60.0);

#if FORK
    private const string Flavour = "fork";
#else
    private const string Flavour = "released";
#endif

    public static int Run(string[] args)
    {
        var label = Arg(args, "--label", Flavour);
        var megabytes = int.Parse(Arg(args, "--mb", "150"));
        var render = Arg(args, "--render", "on") != "off";
        var useBytes = Arg(args, "--bytes", "off") == "on";
        var path = Arg(args, "--file", Path.Combine(Path.GetTempPath(), $"cat{megabytes}mb.txt"));

        EnsureCorpus(path, megabytes);
        var data = File.ReadAllBytes(path);

        if (useBytes && !SupportsByteWrite)
        {
            Console.Error.WriteLine("--bytes needs Write(ReadOnlySpan<byte>), which the released library does not have.");
            return 2;
        }

        Console.WriteLine($"{label}{(useBytes ? " (byte entry)" : "")}: "
                        + $"{data.Length / 1024.0 / 1024.0:N1} MiB, {Cols}x{Rows}, renderer {(render ? "on" : "off")}");

        // Warm first: the tiered JIT needs the hot paths promoted, or the timed pass measures
        // compilation. A tenth of the corpus is enough and costs a fraction of a run.
        var warmLength = Math.Max(1, data.Length / 10);
        RunOnce(data.AsSpan(0, warmLength), render, useBytes, out _);

        var sw = Stopwatch.StartNew();
        RunOnce(data, render, useBytes, out var frames);
        sw.Stop();

        var seconds = sw.Elapsed.TotalSeconds;
        var mib = data.Length / 1024.0 / 1024.0;
        var fps = seconds > 0 ? frames / seconds : 0;

        Console.WriteLine($"  elapsed : {sw.Elapsed.TotalMilliseconds,10:N0} ms");
        Console.WriteLine($"  rate    : {mib / seconds,10:N1} MiB/s");
        Console.WriteLine($"  frames  : {frames,10:N0}  ({fps:N0} fps)");
        return 0;
    }

    private static void RunOnce(ReadOnlySpan<byte> data, bool render, bool useBytes, out long frames)
    {
        var terminal = new Terminal(new TerminalOptions { Cols = Cols, Rows = Rows });
        var renderer = new ViewportRenderer(terminal, Cols, Rows);

        frames = 0;
        var clock = Stopwatch.StartNew();
        var nextFrame = FrameInterval;

        for (var offset = 0; offset < data.Length; offset += ReadSize)
        {
            var take = Math.Min(ReadSize, data.Length - offset);
            Feed(terminal, data.Slice(offset, take), useBytes);

            if (render && clock.Elapsed >= nextFrame)
            {
                renderer.DrawFrame();
                frames++;
                nextFrame = clock.Elapsed + FrameInterval;
            }
        }

        if (render)
        {
            renderer.DrawFrame();   // a terminal settles on a final frame
            frames++;
        }
    }

#if FORK
    private const bool SupportsByteWrite = true;
#else
    private const bool SupportsByteWrite = false;
#endif

    /// <summary>
    /// Hands a PTY read to the terminal.
    ///
    /// The default path decodes to a string per read, because that is what the released library
    /// requires and what the consumer in avalloy-terminal does today — so it is the fair comparison.
    /// --bytes exercises the fork's byte entry instead.
    /// </summary>
    private static void Feed(Terminal terminal, ReadOnlySpan<byte> chunk, bool useBytes)
    {
#if FORK
        if (useBytes)
        {
            terminal.Write(chunk);
            return;
        }
#endif
        terminal.Write(Encoding.UTF8.GetString(chunk));
    }

    private static void EnsureCorpus(string path, int megabytes)
    {
        var target = (long)megabytes * 1024 * 1024;
        if (File.Exists(path) && new FileInfo(path).Length >= target)
            return;

        Console.WriteLine($"generating {megabytes} MiB at {path} ...");

        // Ghostty's test is a plain ASCII file. Fixed seed so a rerun measures the same bytes.
        var rng = new Random(0x5EED);
        const string words = "the quick brown fox jumps over a lazy dog while parsing escape sequences ";

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));

        var line = new StringBuilder(256);
        long written = 0;

        while (written < target)
        {
            line.Clear();
            var width = rng.Next(Cols / 3, Cols);
            var start = rng.Next(words.Length);
            for (var i = 0; i < width; i++)
                line.Append(words[(start + i) % words.Length]);

            writer.Write(line);
            writer.Write('\n');
            written += width + 1;
        }
    }

    private static string Arg(string[] args, string name, string fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }
}

/// <summary>
/// A stand-in for the CPU half of a terminal renderer.
///
/// Walks the visible rows, groups adjacent cells that share an attribute into runs, and materialises
/// each run's text — which is what <c>TerminalView.RenderNormalLine</c> in avalloy-terminal does
/// before handing anything to the graphics layer. No shaping, no rasterisation, no present.
///
/// The result is checksummed rather than discarded so the work cannot be optimised away, and so a
/// divergence between the two libraries would show up as a different number rather than silently not
/// mattering.
/// </summary>
internal sealed class ViewportRenderer
{
    private readonly Terminal _terminal;
    private readonly int _cols;
    private readonly int _rows;
    private readonly StringBuilder _run = new(256);

    public long Checksum { get; private set; }

    public ViewportRenderer(Terminal terminal, int cols, int rows)
    {
        _terminal = terminal;
        _cols = cols;
        _rows = rows;
    }

    public void DrawFrame()
    {
        var buffer = _terminal.Buffer;
        var top = buffer.YDisp;

        for (var y = 0; y < _rows; y++)
        {
            var index = top + y;
            if (index < 0 || index >= buffer.Lines.Length)
                continue;

            var line = buffer.Lines[index];
            if (line == null)
                continue;

            DrawLine(line);
        }
    }

    private void DrawLine(BufferLine line)
    {
        var limit = Math.Min(_cols, line.Length);
        if (limit <= 0)
            return;

        _run.Clear();
        var runAttributes = line[0].Attributes;

        for (var x = 0; x < limit; x++)
        {
            var cell = line[x];

            if (!cell.Attributes.Equals(runAttributes))
            {
                Flush();
                runAttributes = cell.Attributes;
            }

            var content = cell.Content;
            if (string.IsNullOrEmpty(content))
                _run.Append(' ');
            else
                _run.Append(content);
        }

        Flush();
    }

    private void Flush()
    {
        if (_run.Length == 0)
            return;

        // Materialise the run's text, as a renderer must to hand it to a text layer, and fold it into
        // a checksum so nothing here can be elided.
        var text = _run.ToString();
        var hash = 17L;
        for (var i = 0; i < text.Length; i++)
            hash = hash * 31 + text[i];

        Checksum ^= hash;
        _run.Clear();
    }
}
