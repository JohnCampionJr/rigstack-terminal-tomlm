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
cd C:\Code\Pneuma\docker
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
