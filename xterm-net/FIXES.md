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
