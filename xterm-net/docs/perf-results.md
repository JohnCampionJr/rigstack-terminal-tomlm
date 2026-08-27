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

## Keeping it

Two things guard this in CI, held to deliberately different standards.

**The struct layout is a unit test.** `BufferCell` holds no managed references and is 24 bytes.
Neither is a measurement — they hold or they do not — so they run in the ordinary test job and cost
nothing. The reference one is the load-bearing guard: a `string` field added back to the cell would
undo the largest single win here at a stroke, and nothing else in the suite would notice.

**Throughput is a comparison, not a threshold.** `PerfCompare.yml` builds the branch and the commit
it forked from, then runs one harness against both, alternating. Allocation per character is gated
exactly, because bytes allocated for a fixed amount of work is a *count*: it does not care what else
the machine is doing. Time is gated against the spread the job just observed in itself.

That last part is not a preference, it is what the calibration showed. The same build compared
against itself, on a quiet laptop:

| work per corpus | apparent Δ on scroll-ascii | spread |
|---|---|---|
| 60M chars | **+27%** | ±30% |
| 300M chars | +0.2% | ±1% |

A fixed threshold would have had to sit above 30% to survive the first row, which is far too loose to
catch anything real — and the first row is also where `scroll-ascii` read 3.9 ns/char against the
1.7 it actually runs at, because the work was too short to finish warming. So the gate is
`max(5%, 3 × observed spread)`: a quiet machine earns a tight gate, a busy one raises its own bar
rather than crying wolf, and anything between the floor and the gate is reported as worth a look
instead of vanishing.

Checked against a regression rather than assumed to work: removing the `_placeholderCell` guard from
`Print` — a real 12% found by hand while merging Kitty — was flagged at +11.2% against a 7.0% gate,
with the other five corpora silent.

The harness deliberately touches only `Terminal`, `TerminalOptions` and `Write(string)`. That is what
lets one build of it measure an older library by assembly substitution; anything newer would fail at
run time and the job could then only ever compare a build against itself. It reports the module
version id of what it loaded for the same reason — two runs of the same assembly would otherwise
report a flawless result and mean nothing.

```
dotnet run --project src/XTerm.NET.Bench -c Release -- ci --out head.json
dotnet run --project src/XTerm.NET.Bench -c Release -- compare --base a.json b.json --head c.json d.json
```
