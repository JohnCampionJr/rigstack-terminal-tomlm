# Resize reflow for the normal buffer

## Summary

This change ports xterm.js 5.5.0 resize reflow into XTerm.NET. Shrinking column width re-wraps long logical lines onto additional `IsWrapped` rows instead of truncating them; growing width merges wrapped groups back. The alternate buffer is excluded via an explicit `hasScrollback: false` constructor flag.

A related latent bug is fixed: when the buffer was at capacity and row count shrank, `CircularList.Resize` ran before trimming and kept the oldest lines, silently discarding the live screen bottom. Resize now trims from the top (raising `Trimmed`) before shrinking capacity.

A second bug, in the reflow itself, is fixed here: shrinking a buffer that held an EMPTY wrapped group threw `IndexOutOfRangeException`. `ReflowSmallerGetNewLineLengths` loops while `cellsAvailable < cellsNeeded`, so a group whose trimmed length is zero returns an empty array, and `ReflowSmaller` read `[Length - 1]` from it. Only a one-row group can be empty -- every row of a group except the last counts as a full row of cells regardless of content -- so it takes a blank continuation row at index 0 with an unwrapped row beneath, which is what the scrollback leaves once the row being continued is trimmed away. Twelve spaces at six columns, two further lines, and a narrowing resize reproduce it through `Terminal.Write` alone.

## Why

Without reflow, shrinking a terminal window and growing it back left every long line permanently truncated at the narrowest width — the primary defect tracked as ISS-007. Scrollback lines were also lost on capacity shrink because `CircularList.Resize` preserved the wrong end of the buffer.

## Resize edge cases found in review

Six further defects, each reproduced before being fixed and each reachable from ordinary use:

- **One-column reflow hung, then threw `OutOfMemoryException`.** A wide glyph at the wrap boundary made the new line length zero, so `ReflowSmallerGetNewLineLengths` never advanced and appended rows until the list could not grow. A wide glyph cannot be shown in one column, so it is clipped.
- **The viewport adjustment popped rows the outer loop was still walking**, throwing `IndexOutOfRangeException`.
- **A line expanding past the remaining capacity indexed below zero** in the batched rebuild, throwing. Rows that do not fit are the oldest, which capacity trimming discards anyway.
- **`Math.Min` dropped the cursor's lower bound.** Moving to the new column count was the point of that change, but a negative cursor -- which `SetCursorRaw` exists to allow -- survived the resize and left the buffer reporting an out-of-bounds position.
- **The viewport was shifted by the trim amount rather than recomputed.** A 5-row buffer with 5 of scrollback resized to 3 rows showed rows 3..5 of 8, with the live bottom unseen at row 7 and later output landing outside the visible area.
- **A zero-row buffer could never be initialised by a later resize**, because the row-fill loop had moved inside a "has lines" guard. `Lines.Length` stayed 0 and the next write indexed an empty list.

Two of these are the same root cause: this is a port from JavaScript, where reading past the end of an array yields `undefined` and falls into a null check. In C# the identical read throws.

## Files changed

- `src/XTerm.NET/Buffer/BufferReflow.cs` — pure reflow functions ported from `BufferReflow.ts`
- `src/XTerm.NET/Buffer/TerminalBuffer.cs` — `Resize` restructure, `ReflowLarger`/`ReflowSmaller`, `hasScrollback` flag
- `src/XTerm.NET/Buffer/BufferLine.cs` — `GetWidth`, `HasContent`, `ReplaceCells`; `GetTrimmedLength` wide-char width
- `src/XTerm.NET/Buffer/CircularList.cs` — `SetLength` for reflow batching
- `src/XTerm.NET/Terminal.cs` — alt buffer `hasScrollback: false`
- `src/XTerm.NET.Tests/Buffer/BufferReflowTests.cs` — pure-function tests
- `src/XTerm.NET.Tests/Buffer/BufferTests.cs` — reflow integration tests
- `src/XTerm.NET.Tests/Buffer/ReflowEmptyGroupTests.cs` — regression tests for the empty wrapped group
- `src/XTerm.NET.Tests/Buffer/ResizeEdgeCaseTests.cs` — regression tests for the six resize edge cases above

## Validation

```powershell
dotnet test src/XTerm.NET.slnx
```

Result on this branch:

```text
Passed: 728
Failed: 0
Skipped: 0
```

# Docker progress rendering fixes

## Summary

This branch fixes a terminal cell-width bug that affected Docker Compose progress output in Termrig and adds regression coverage around the related VT behavior.

Termrig reference links:

- Project: https://github.com/jchristn/Termrig
- Branch containing the downstream reproduction, compatibility workaround, and PTY recorder: https://github.com/jchristn/Termrig/tree/fix/terminal
- Termrig commit that added the first-class PTY recorder used for future raw-byte captures: https://github.com/jchristn/Termrig/commit/3a075e3acadb3d9b3815f8d27209dc13d63787f8
- Terminal integration code that consumes XTerm.NET through the Avalonia terminal control: https://github.com/jchristn/Termrig/tree/fix/terminal/src/ThirdParty/Iciclecreek.Avalonia.Terminal

The confirmed upstream defect was that `InputHandler.GetStringCellWidth` treated any code point classified by `NeoSmart.Unicode.Emoji.IsEmoji` as width 2. That makes U+2714 HEAVY CHECK MARK (`\u2714`) consume two terminal cells even when it is emitted in text presentation. Docker Compose uses that character without U+FE0F emoji presentation, and Windows `cmd.exe` renders it as a single-cell icon. XTerm.NET therefore shifted the rest of those progress rows by one cell.

The fix is to use `Wcwidth.UnicodeCalculator.GetWidth` for the base width and keep the existing variation-selector handling:

- `\u2714` remains width 1.
- `\u2714\uFE0F` becomes width 2 because U+FE0F explicitly requests emoji presentation.
- Existing wide emoji and CJK width behavior continues to come from `UnicodeCalculator`.
# Origin mode and scroll region fixes

## Summary

This change fixes DEC origin-mode cursor positioning with scroll regions. These changes are core terminal emulator behavior and are not specific to Termrig, Avalonia, ConPTY, or any host renderer.

The fixed behavior is:

- `DECSTBM` / `CSI t;b r` moves the cursor to home after setting the scroll region.
- `CUP` / `CSI row;col H` and `HVP` / `CSI row;col f` treat row coordinates as relative to the scroll region when `DECOM` / origin mode is enabled.
- `VPA` / `CSI row d` applies the same origin-mode row translation.
- enabling origin mode moves the cursor to the top margin of the scroll region; disabling origin mode moves the cursor to absolute home.

## Why

Full-screen and prompt-oriented terminal applications often reserve a bottom input or status row by setting a scroll region for the output area. They then use origin-mode cursor addressing inside that region.

If the emulator treats those row coordinates as absolute screen rows, application output can be written outside the intended scroll region. In real-world terminal UIs this can leave stale prompt/status rows in scrollback or place rewritten content on the wrong line.

## Files changed

- `src/XTerm.NET/InputHandler.cs`
  - Replaced broad emoji classification width override with Unicode cell-width calculation.
- `src/XTerm.NET.Tests/InputHandlerTests.cs`
  - Added regression tests for text-presentation checkmark width.
  - Added regression tests for emoji-presentation checkmark width.
  - Added a Docker-style progress alignment test.
  - Added explicit coverage for `CSI Ps C` cursor-forward clamping.
  - Added explicit coverage for `CSI Ps X` erase-character preserving cursor position.

## Reproduction

### Docker Compose command

Run Docker Compose in a narrow-ish terminal where Compose emits progress rows:

```cmd
cd <path-to-your-compose-project>
docker compose up -d
docker compose down
```

Expected output, matching Windows `cmd.exe`, keeps the status column aligned:

```text
 ✔ Network docker_default              Created
 ✔ Container docker-litegraph-1        Healthy
```

Before this fix, rows using `\u2714` could render one cell short before the status text:

```text
 ✔ Network docker_default             Created
```

The missing space is caused by XTerm.NET counting `\u2714` as two cells while Docker/cmd treat it as one text cell.

### Minimal checkmark-width reproduction

```csharp
var terminal = new Terminal(new TerminalOptions { Cols = 20, Rows = 3 });
terminal.Write("\u2714X");

// Expected:
// cursor X == 2
// cell 0 contains \u2714 with Width == 1
// cell 1 contains X with Width == 1
```

Before this fix:

```text
cursor X == 3
cell 0 Width == 2
cell 1 was a spacer
cell 2 contained X
```

### Emoji-presentation checkmark

The variation selector case must still be double-width:

```csharp
var terminal = new Terminal(new TerminalOptions { Cols = 20, Rows = 3 });
terminal.Write("\u2714\uFE0FX");

// Expected:
// cursor X == 3
// first glyph Width == 2
// following spacer Width == 0
// X Width == 1
```

This verifies that the fix does not flatten explicit emoji presentation to single width.

### Docker-style status column

Docker Compose progress rows use a text icon, a resource kind/name, cursor movement, then a status:

```csharp
var terminal = new Terminal(new TerminalOptions { Cols = 80, Rows = 3 });
const string prefix = " \u2714 Network docker_default";

terminal.Write(prefix);
terminal.Write("\x1B[28C");
terminal.Write("Created");

int statusColumn = prefix.Length + 28;
Assert.Equal("Created", terminal.Buffer.Lines[0]!.TranslateToString(false, statusColumn, statusColumn + 7));
```

With a two-cell checkmark, this status starts one cell later than expected.

## Related VT behavior verified

The Docker progress stream also uses cursor and erase sequences heavily. The branch adds tests documenting the intended behavior so future changes do not regress it.

### `CSI Ps X` erase-character

`CSI Ps X` erases `Ps` cells from the current cursor position. It must not move the cursor and must not wrap.

```csharp
var terminal = new Terminal(new TerminalOptions { Cols = 20, Rows = 3 });
terminal.Write("abcdef");
terminal.Buffer.SetCursor(2, 0);
terminal.Write("\x1B[3X");

// Expected line: "ab   f"
// Expected cursor: X == 2, Y == 0
```

### `CSI Ps C` cursor-forward

`CSI Ps C` moves right but clamps at the right margin. It must not wrap to the next row.

```csharp
var terminal = new Terminal(new TerminalOptions { Cols = 10, Rows = 3 });
var handler = new InputHandler(terminal);
terminal.Buffer.SetCursor(7, 0);

var parameters = new Params();
parameters.AddParam(20);
handler.HandleCsi("C", parameters);

// Expected cursor: X == 9, Y == 0
```

## Termrig compatibility note

Termrig currently has a local Docker progress normalizer/workaround on the `fix/terminal` branch that trims trailing line-ending padding and rewrites some Docker progress cursor sequences before passing output into XTerm.NET. That workaround was useful while diagnosing overlapping and duplicate progress rows. Once Termrig consumes an XTerm.NET version that includes this width fix and any future upstream parser fixes, Termrig should re-test without the local normalizer and remove as much of that workaround as possible.

The first-class PTY recorder added to Termrig should be used for future reports. It records raw PTY bytes before any normalization, which makes reproductions suitable for XTerm.NET issues and pull requests.
  - Added shared row translation for origin-mode cursor addressing.
  - Applied that translation to `CUP` / `HVP` and `VPA`.
  - Homed the cursor after `DECSTBM`.
  - Homed to the top margin when origin mode is enabled.
- `src/XTerm.NET.Tests/InputHandlerTests.cs`
  - Added regression coverage for scroll-region cursor homing.
  - Added regression coverage for origin-relative `CUP` / `HVP`.
  - Added regression coverage for origin-relative `VPA`.
- `src/XTerm.NET.Tests/ModeHandlingTests.cs`
  - Added regression coverage for enabling origin mode with a non-zero top margin.

## Minimal reproductions

### Scroll region homes the cursor

```csharp
var terminal = new Terminal(new TerminalOptions { Cols = 20, Rows = 5 });
var handler = new InputHandler(terminal);
terminal.Buffer.SetCursor(10, 4);

var parameters = new Params();
parameters.AddParam(2);
parameters.AddParam(4);
handler.HandleCsi("r", parameters);

Assert.Equal(0, terminal.Buffer.X);
Assert.Equal(0, terminal.Buffer.Y);
```

### Origin-mode `CUP` is relative to the scroll region

```csharp
var terminal = new Terminal(new TerminalOptions { Cols = 20, Rows = 5 });
var handler = new InputHandler(terminal);
terminal.Buffer.SetScrollRegion(1, 3);
terminal.OriginMode = true;

var parameters = new Params();
parameters.AddParam(3);
parameters.AddParam(20);
handler.HandleCsi("H", parameters);

Assert.Equal(19, terminal.Buffer.X);
Assert.Equal(3, terminal.Buffer.Y);
```

### Origin-mode `VPA` is relative to the scroll region

```csharp
var terminal = new Terminal(new TerminalOptions { Cols = 20, Rows = 5 });
var handler = new InputHandler(terminal);
terminal.Buffer.SetScrollRegion(1, 3);
terminal.OriginMode = true;
terminal.Buffer.SetCursor(10, 1);

var parameters = new Params();
parameters.AddParam(3);
handler.HandleCsi("d", parameters);

Assert.Equal(10, terminal.Buffer.X);
Assert.Equal(3, terminal.Buffer.Y);
```

## Not included

This change intentionally does not include:

- host-rendering changes
- PTY or ConPTY line-ending policy
- Avalonia integration changes
- Termrig-specific output normalization
- Docker Compose cell-width fixes from the earlier Docker progress branch

Those are separate concerns. This change is limited to standard VT scroll-region and origin-mode semantics in XTerm.NET.

## Validation

Run from this repository root:

```powershell
dotnet test src/XTerm.NET.slnx
```

Result on this branch:

```text
Passed: 589
Failed: 0
Skipped: 0
```
dotnet test src/XTerm.NET.slnx --no-restore
```

Expected result: all tests pass.

---

# Sixel graphics

## Summary

Sixel images (`ESC P … q … ESC \`) are decoded and placed in the buffer. Each cell an image covers
carries a reference to one shared, immutable `TerminalImage` plus the coordinates of the tile it
shows, so a picture behaves like terminal content rather than an overlay.

Sixel was not merely unimplemented before this — it was unreachable. `EscapeSequenceParser`
collapsed `DcsEntry`/`DcsParam`/`DcsIgnore`/`DcsPassthrough` into a single "discard every byte until
ST" case, `_dcs` was allocated but never written to, and the `Dcs` event was marked `[Obsolete]`
because nothing raised it. The payload never reached anything that could decode it.

## Why storage on the cell

`BufferCell` is a struct, and `InputHandler.Print` builds a fresh one for every character. That
single fact gives the whole feature its semantics for free:

| Terminal action | Existing mechanism | Effect on images |
|---|---|---|
| Print over an image cell | `new BufferCell{…}` + `SetCell` | the new struct has no image, so that cell reverts to text |
| ED / EL / ECH / DECALN | `Fill`/`ReplaceCells` with `BufferCell.Space` | tiles cleared |
| Scroll / scrollback | `CircularList` moves whole `BufferLine` objects | tiles ride along |
| Line trimmed from scrollback | line dereferenced | the image is collected with its last tile |
| Selection / `TranslateToString` | reads `cell.Content` | image cells hold `" "`, so copying yields blanks |

A reference to a shared image rather than a per-cell bitmap slice: identical overwrite granularity,
one allocation per image instead of columns times rows, and a host can coalesce a run of adjacent
tiles into a single draw call. `BufferCell` grows from 32 to 40 bytes — the packed tile `int` fits
in padding the reference already forces — which is roughly 0.65 MB on an 80-column buffer with 1000
lines of scrollback.

## Two live bugs fixed along the way

- **`CSI ? 1;1;0 S` scrolled the screen.** XTSMGRAPHICS shares its final character with SCROLL UP,
  and `ToCsiCommand` strips the private marker before the lookup, so a graphics capability query
  was routed to the scroll handler. Every Sixel-capable program sends one during startup, which
  made this routine rather than obscure. `ScrollUp` is now guarded on `isPrivate` and the query is
  answered.
- **The primary DA reply did not advertise Sixel.** `libsixel`, `chafa`, `img2sixel` and everything
  built on them read attribute `4` from `CSI c` and send text art instead of pictures without it.
  The reply is now `CSI ? 1 ; 2 ; 4 c`, following `Options.SixelEnabled` so it never claims a
  capability that is switched off.

## Decisions worth recording

- **Images are dropped on a column resize.** Reflow re-wraps a logical line by copying ranges of
  cells between lines; tiles carried through it would reassemble as a shuffled mosaic — every piece
  intact, in the wrong place. A change of row count alone moves whole lines and keeps them.
- **The DCS payload is streamed, not buffered.** A full-screen Sixel runs to hundreds of kilobytes.
  The parser raises `DcsHook`/`DcsPut`/`DcsUnhook` and only accumulates a whole-payload string for
  the legacy `Dcs` event when something is subscribed and the sequence stays under 4 KB.
- **An abandoned sequence is distinguishable from a finished one.** `DcsUnhook` reports whether a
  string terminator ended it, so a truncated image is discarded rather than half-drawn. An `ESC`
  mid-payload is resolved one character late, since `ESC \` terminates and anything else abandons.
- **Sixel colour registers are kept apart from `ColorPalette`.** They are a separate numbering that
  an image may redefine as it draws, and doing that to the palette the renderer reads on its hot
  path would repaint the text as a side effect of showing a picture.
- **Nothing in the decoder throws.** The payload is untrusted output from another process; a
  nonsense register, an absurd repeat count or a truncated stream yields no image, not an exception
  escaping into the parser.
- **A host must answer the window queries from the grid, not from its control.** Not a change here
  -- an unhandled query still produces no reply, deliberately -- but the reason the README now spells
  the handler out. An image viewer works out the cell size for itself by dividing the pixel size from
  `CSI 14 t` by the row count it already has, so anything else in that figure (a scrollbar, window
  chrome, or the strip below the last row, since the grid is a truncated division) is read back as
  picture that does not fit. It runs off the bottom and scrolls the screen. The only safe answer is
  `Cols * CellWidthPixels` by `Rows * CellHeightPixels`, which is also what xterm reports.
- **The image budget is swept by the byte, not by the picture.** A program animating with Sixel
  draws one image per frame, and sweeping on each would walk every cell of both buffers ten times a
  second. Bytes placed are counted instead, and a sweep runs only once a budget's worth has arrived
  — one scan per budget rather than one per picture, at the cost of the buffer sitting up to one
  budget over before it is trimmed.

## Files changed

- `src/XTerm.NET/Parser/EscapeSequenceParser.cs` — real DCS state machine; streaming hook/put/unhook
- `src/XTerm.NET/Common/Types.cs` — `ParserState.DcsIntermediate`
- `src/XTerm.NET/Events/ParserEvents.cs` — `DcsHookEventArgs`, `DcsPutEventArgs`, `DcsUnhookEventArgs`
- `src/XTerm.NET/Graphics/TerminalImage.cs` — immutable BGRA image plus tile geometry
- `src/XTerm.NET/Graphics/SixelDecoder.cs` — streaming DECSIXEL decoder
- `src/XTerm.NET/Graphics/SixelPalette.cs` — VT340 defaults, RGB and HLS colour
- `src/XTerm.NET/Buffer/BufferCell.cs` — `Image`, packed `ImageTile`, equality
- `src/XTerm.NET/Buffer/BufferLine.cs` — `ClearImages`, `HasImages`
- `src/XTerm.NET/Buffer/TerminalBuffer.cs` — `ClearImages`, dropped on column resize
- `src/XTerm.NET/InputHandler.cs` — DCS dispatch, `PlaceImage`, DA, modes 80/1070/8452, XTSMGRAPHICS
- `src/XTerm.NET/Terminal.cs` — parser wiring, Sixel mode flags, `EnforceImageBudget`
- `src/XTerm.NET/Options/TerminalOptions.cs` — `SixelEnabled`, cell pixel size, budgets
- `src/XTerm.NET.Tests/Parser/DcsSequenceTests.cs`
- `src/XTerm.NET.Tests/Graphics/SixelDecoderTests.cs`
- `src/XTerm.NET.Tests/Graphics/SixelPlacementTests.cs`
- `src/XTerm.NET.Tests/Graphics/ImageCellLifetimeTests.cs`
- `src/XTerm.NET.Tests/Graphics/GraphicsAttributesTests.cs`

## Validation

```powershell
dotnet test src/XTerm.NET.slnx
```

```text
Passed: 847
Failed: 0
Skipped: 0
```
