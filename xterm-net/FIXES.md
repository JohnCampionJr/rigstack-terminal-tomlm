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
dotnet test src/XTerm.NET.slnx --no-restore
```

Expected result: all tests pass.
