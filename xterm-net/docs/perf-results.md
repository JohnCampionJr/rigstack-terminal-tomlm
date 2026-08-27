# Emulator performance — results

What this fork changed and what it measured. The wider story, including the renderer
work and the Ghostty comparison it was all aimed at, is in
[term-perf/docs/ghostty-class-terminal.md](https://github.com/JohnCampionJr/term-perf/blob/perf-base/docs/ghostty-class-terminal.md).

Fork point: upstream XTerm.NET `0f50d60`. 14 commits, 801 tests green throughout, no
public API broken. All figures from one machine — MacBook Pro, macOS 26.5.1, arm64.

---

## Throughput, 240×67

| corpus | before | after | speedup |
|---|---|---|---|
| scroll-ascii | 24.0 | **126.4** MiB/s | 5.3× |
| sgr-churn | 34.6 | **142.9** | 4.1× |
| truecolor | 91.8 | **221.2** | 2.4× |
| alt-redraw | 28.0 | **106.9** | 3.8× |
| unicode | 22.3 | **64.6** | 2.9× |
| flood | 7.4 | **18.9** | 2.6× |

## Allocation, per printed character

| corpus | before | after |
|---|---|---|
| scroll-ascii | 119.4 bytes | **0.0** |
| flood | 2,624 bytes | **0.0** |
| sgr-churn | 66.4 | 2.3 |
| unicode | 106.3 | 4.1 |

`scroll-ascii` and `flood` provoke zero gen0 collections. What remains in `unicode` is
ZWJ grapheme clusters, which are genuinely unbounded strings a cell has to hold.

## End to end

Ghostty's 150 MB cat test, through the real Avalonia renderer, same harness with only
the emulator swapped:

| | elapsed | rate |
|---|---|---|
| XTerm.NET 1.1.0 | 16,176 ms | 9.3 MiB/s |
| this fork | **1,259 ms** | 119.2 MiB/s |

**12.8×.**

---

## What the cost actually was

Not parsing. Per printed glyph: a string and an `EventArgs` from the parser's Print
event, and another string from charset translation. Per CSI sequence: a five-object
`Params` clone defending against retention that never happens. Per character: a
dictionary lookup to resolve the active charset.

The shape recurred — the parser raised events the terminal was the only mandatory
listener for. `?.Invoke(this, new Args())` *does* short-circuit when unsubscribed;
the terminal was simply subscribed to all of them.

## Levers, measured before they were pulled

| | before | after | ratio |
|---|---|---|---|
| `UnicodeCalculator.GetWidth` vs a table | 22.86 ns | 0.32 ns | **71.6×** |
| `sizeof(BufferCell)` | 32 B | 24 B | — |
| `Array.Fill` of 240 cells | 238.3 ns | 75.0 ns | 3.2× |
| per-cell assignment | 0.88 ns | 0.35 ns | 2.6× |

The last three are one change: taking the `string` out of `BufferCell` made it
blittable, so the runtime stopped emitting a GC write barrier per cell and stopped
tracing the entire scrollback. `Content` survives as a derived property, which is why
it landed without editing a single call site.

## Byte entry

`Write(ReadOnlySpan<byte>)`, with the transcode charged to the string path since a pty
consumer cannot avoid it:

| corpus | string | bytes | ratio |
|---|---|---|---|
| scroll-ascii | 483.8 | 594.2 MiB/s | 1.23× |
| truecolor | 211.9 | 256.8 | 1.21× |
| unicode | 111.3 | 128.8 | 1.16× |
| alt-redraw | 312.2 | 300.5 | **0.96×** |

Modest, and `alt-redraw` is slightly slower — its three-byte box-drawing sequences
cost more through `Rune.DecodeFromUtf8` than reading a char something else already
decoded. The throughput was not the prize: per-read allocation goes to zero, and the
entry fixes a real defect. **A multi-byte character split across a read boundary was
corrupted**, because decoding each read alone cannot see a sequence continuing into
the next. Pty reads end wherever they end.

## Also fixed

A surrogate pair split across two `Write` calls produced two U+FFFD instead of one
character. Pre-existing — `EnumerateRunes` cannot carry state between calls either.
The parser now holds a trailing high surrogate until the next chunk resolves it.

---

## Harness

`src/XTerm.NET.Bench`, modes:

| mode | what it answers |
|---|---|
| `alloc` | throughput and bytes allocated per character, per corpus |
| `bench` | BenchmarkDotNet sweep with MemoryDiagnoser |
| `soak` | tight parse loop, long enough to attach `dotnet-trace` |
| `flood` | attributes the newline path — CR, LF, recycling on and off |
| `unicode` | attributes allocation by content class |
| `width` | codepoint width: library call against a table |
| `layout` | `BufferCell` size and fill cost, with and without a reference field |

Corpora are generated from a fixed seed, so a later run measures the same bytes.

```bash
dotnet run -c Release --project src/XTerm.NET.Bench -- alloc --seconds 2
```

## Notes on measuring

Two things that cost real work and are worth not repeating.

**Warm to convergence, not for a fixed count.** Throughput climbs 3.1 → ~10 MiB/s and
only settles after three or four full passes, as tiered compilation promotes the
parser's hot methods. Timing before that measures the JIT and understates steady
state by ~3×.

**The sampling profiler was misleading twice** — once attributing 92% of self time to
a loop that only calls `Write`, once hiding `Print` inside `ParseChar` through
inlining. What worked instead was attribution by isolating one variable at a time,
which is what the `flood`, `unicode`, `width` and `layout` probe modes exist for.

## Open

- Per-scroll `Array.Fill` of a whole line. A per-line high-water mark would fix it,
  but has to be updated at every cell-write site, and missing one leaves stale text
  after a scroll.
- Kitty keyboard protocol — absent. xterm.js added it in
  [PR #5600](https://github.com/xtermjs/xterm.js/pull/5600), merged January 2026,
  after this port was taken.
- Kitty graphics and Sixel — absent. The parser recognises APC and DCS and discards
  the payload.
