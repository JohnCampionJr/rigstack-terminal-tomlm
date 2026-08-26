# XTerm.NET

[![Build Status](https://github.com/tomlm/XTerm.NET/actions/workflows/BuildAndRunTests.yml/badge.svg)](https://github.com/tomlm/XTerm.NET/actions/workflows/BuildAndRunTests.yml) [![NuGet Version](https://img.shields.io/nuget/v/XTerm.NET.svg)](https://www.nuget.org/packages/XTerm.NET/) [![NuGet Downloads](https://img.shields.io/nuget/dt/XTerm.NET.svg)](https://www.nuget.org/packages/XTerm.NET/) [![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

A .NET terminal emulator library inspired by [xterm.js](https://github.com/xtermjs/xterm.js).
XTerm.NET provides a headless terminal emulator that parses and processes VT100/ANSI escape sequences,
making it easy to host console applications in your .NET applications.

**Sixel graphics are supported.** Images arrive as ordinary cell content rather than as an overlay, so
they are overwritten by text, cleared by `ED`/`EL`, scrolled with their lines, and freed when they fall
out of the scrollback — see [Sixel Images](#sixel-images).

## Features

- **Full VT100/ANSI Escape Sequence Support** — Process colors, cursor movement, text attributes, and more
- **Headless Design** — No UI dependencies; bring your own renderer (Console, WPF, MAUI, etc.)
- **Dual Buffer Support** — Normal and alternate screen buffers with scrollback
- **Keyboard & Mouse Input Generation** — Generate escape sequences for keyboard and mouse events
- **Rich Event System** — Subscribe to terminal events like title changes, bell, resize, and window manipulation
- **256 and True Color Support** — Full RGB and 256-color palette support
- **Unicode Support** — Proper handling of wide characters and Unicode text
- **Sixel Graphics** — Decodes Sixel images (`ESC P … q`) and stores them on the cells they cover, so
  `img2sixel`, `chafa`, `lsix` and `timg` work against a host that renders them

## Installation

Install via NuGet Package Manager:

```shell
dotnet add package XTerm.NET
```

Or via the Package Manager Console in Visual Studio:

```powershell
Install-Package XTerm.NET
```

## Usage
The basic architecture is that the Terminal is a XxY array of Buffer Cell structures which represent each cell of the console screen.

* Incoming text from a hosted process is written to the terminal and the terminal will interpret any ANSI VT Escape codes to 
change color, underline, position etc.
* The terminal host application calls Terminal.GenerateMouseEvent(), Terminal.GenerateKeyEvent() to send input to the console process.
* Requests for information are modeled as events (GetWindowTitle, SetWindowTitle etc.).

### Creating a Terminal

Create a terminal instance with default settings (80 columns × 24 rows):

```csharp
using XTerm;

var terminal = new Terminal();
```

Or customize the terminal with `TerminalOptions`:

```csharp
using XTerm;
using XTerm.Options;

var terminal = new Terminal(new TerminalOptions
{
    Cols = 120,                   // Number of columns
    Rows = 40,                    // Number of rows
    Scrollback = 1000,            // Scrollback buffer lines (0 to disable)
    CursorStyle = CursorStyle.Block,
    CursorBlink = true,
    TermName = "xterm"            // Terminal type for identification
});
```

### Resizing the Terminal

Resize the terminal dynamically to match your UI or window size:

```csharp
// Resize to new dimensions
terminal.Resize(cols: 120, rows: 50);

// Query current size
int currentCols = terminal.Cols;
int currentRows = terminal.Rows;
```

### Writing Content to the Terminal

Write text and ANSI escape sequences to the terminal:

```csharp
// Write text (no automatic newline)
terminal.Write("Hello, ");

// Write a line (adds \r\n)
terminal.WriteLine("XTerm.NET!");

// Write with ANSI colors and styles
terminal.WriteLine("\x1b[31mRed text\x1b[0m");
terminal.WriteLine("\x1b[1;32mBold green text\x1b[0m");
terminal.WriteLine("\x1b[38;2;255;100;200mTrue color (RGB) text\x1b[0m");

// Position the cursor and draw
terminal.Write("\x1b[5;10HText at row 5, column 10");
```

Access the buffer to read terminal content:

```csharp
var buffer = terminal.Buffer;

// Get cursor position
int cursorX = buffer.X;
int cursorY = buffer.Y;

// Read a line as a string
string lineContent = terminal.GetLine(0);

// Or access the buffer line directly
var line = buffer.Lines[0];
string content = line?.TranslateToString(trimRight: true) ?? "";
```

### Hooking Up Events

Subscribe to events to integrate the terminal into your application:

```csharp
// Data sent back from the terminal (e.g., query responses)
terminal.DataReceived += (sender, e) =>
{
    // Send e.Data to your connected process/PTY
    Console.WriteLine($"Terminal sent: {e.Data}");
};

// Terminal title changed (via OSC escape sequence)
terminal.TitleChanged += (sender, e) =>
{
    // Update your window title
    Console.WriteLine($"Title: {e.Title}");
};

// Terminal resized
terminal.Resized += (sender, e) =>
{
    // Notify your PTY/process of the new size
    Console.WriteLine($"Resized to {e.Cols}x{e.Rows}");
};

// Bell character received
terminal.BellRang += (sender, e) =>
{
    // Play a sound or flash the window
    Console.WriteLine("Bell!");
};

// Line feed occurred (useful for tracking output)
terminal.LineFed += (sender, e) =>
{
    // Trigger a render update
};

// Cursor style changed
terminal.CursorStyleChanged += (sender, e) =>
{
    // Update cursor rendering
    Console.WriteLine($"Cursor: {e.Style}, Blink: {e.Blink}");
};

// Buffer switched (normal ↔ alternate)
terminal.BufferChanged += (sender, e) =>
{
    Console.WriteLine($"Switched to {e.BufferType} buffer");
};
```

**Window manipulation events** (used by some terminal applications):

```csharp
terminal.WindowMoved += (sender, e) => Console.WriteLine($"Move to ({e.X}, {e.Y})");
terminal.WindowResized += (sender, e) => Console.WriteLine($"Resize to {e.Width}x{e.Height}");
terminal.WindowMinimized += (sender, e) => Console.WriteLine("Minimize");
terminal.WindowMaximized += (sender, e) => Console.WriteLine("Maximize");
terminal.WindowRestored += (sender, e) => Console.WriteLine("Restore");
```

### Rendering the Buffer

XTerm.NET is headless — you provide the rendering logic for your UI framework (Console, WPF, MAUI, Avalonia, etc.). Walk over the terminal buffer and render each cell according to its content and attributes:

```csharp
void RenderTerminal(Terminal terminal)
{
    var buffer = terminal.Buffer;

    for (int row = 0; row < terminal.Rows; row++)
    {
        var line = buffer.Lines[buffer.YDisp + row];
        if (line == null) continue;

        for (int col = 0; col < terminal.Cols; col++)
        {
            BufferCell cell = line[col];

            // Skip empty cells or continuation cells (wide character's second cell)
            if (cell.Width == 0) continue;

            // Get the character content
            string character = cell.Content;

            // Get foreground/background colors
            int fgColor = cell.Attributes.GetFgColor();
            int bgColor = cell.Attributes.GetBgColor();
            int fgMode = cell.Attributes.GetFgColorMode();  // 0=default, 1=256-color, 2=RGB
            int bgMode = cell.Attributes.GetBgColorMode();

            // Check text style attributes
            bool isBold = cell.Attributes.IsBold();
            bool isDim = cell.Attributes.IsDim();
            bool isItalic = cell.Attributes.IsItalic();
            bool isUnderline = cell.Attributes.IsUnderline();
            bool isBlink = cell.Attributes.IsBlink();
            bool isInverse = cell.Attributes.IsInverse();
            bool isInvisible = cell.Attributes.IsInvisible();
            bool isStrikethrough = cell.Attributes.IsStrikethrough();
            bool isOverline = cell.Attributes.IsOverline();

            // Render the cell at (col, row) with the appropriate styling
            // Your rendering code here — e.g., DrawText(col, row, character, fg, bg, styles...)
        }
    }

    // Render the cursor if visible
    if (terminal.CursorVisible)
    {
        int cursorX = buffer.X;
        int cursorY = buffer.Y;
        CursorStyle style = terminal.Options.CursorStyle;  // Block, Underline, or Bar
        bool blink = terminal.Options.CursorBlink;

        // Draw cursor at (cursorX, cursorY) with the appropriate style
    }
}
```

**Color mode values:**
- `0` — Default terminal color (use theme foreground/background)
- `1` — 256-color palette index (0–255)
- `2` — True color RGB (extract with `color & 0xFF` for each channel)

**Handling wide characters:**

Wide characters (e.g., CJK ideographs, emoji) have `Width = 2`. The first cell contains the character, and the second cell has `Width = 0` as a placeholder — skip it during rendering but allocate space for the double-width glyph.

### Sixel Images

A Sixel image (`ESC P … q … ESC \`) is decoded and written into the cells it covers. Each covered
cell carries a reference to one shared `TerminalImage` plus the coordinates of the piece it shows,
so an image behaves like terminal content rather than an overlay: printing over a cell replaces
that part of the picture, `ED`/`EL` clear it, scrolling carries it, and the image is freed once the
last cell holding it is gone.

**Tell the terminal your cell size.** XTerm.NET is headless and cannot measure a font, so it cannot
work out how many columns an image covers unless you say. Set these from your renderer's metrics,
in *device* pixels:

```csharp
terminal.Options.CellWidthPixels  = 8;
terminal.Options.CellHeightPixels = 17;
```

**Answer the window queries from these same numbers.** An image viewer works out the cell size for
itself, by dividing the pixel size it gets from `CSI 14 t` by the row and column counts it already
has. So your `WindowInfoRequested` handler must report the **grid**, not the control:

```csharp
case WindowInfoRequest.SizePixels:      // CSI 14 t
    e.WidthPixels  = terminal.Cols * terminal.Options.CellWidthPixels;
    e.HeightPixels = terminal.Rows * terminal.Options.CellHeightPixels;
    e.Handled = true;
    break;

case WindowInfoRequest.CellSizePixels:  // CSI 16 t
    e.CellWidth  = terminal.Options.CellWidthPixels;
    e.CellHeight = terminal.Options.CellHeightPixels;
    e.Handled = true;
    break;
```

Reporting your control's own size instead is the classic way to get this wrong. It includes the
scrollbar, any window chrome, and the strip below the last row — the grid is a truncated division, so
up to a whole row of the control's height belongs to no row at all. An application dividing that
figure by the row count is told the terminal is taller than it is, sizes a picture to fill it, and the
surplus runs off the bottom and scrolls the screen.

**Rendering the tiles.** Extend the per-cell loop above:

```csharp
BufferCell cell = line[col];

if (cell.Image is TerminalImage image &&
    image.TryGetTileSource(cell.ImageCol, cell.ImageRow, out int sx, out int sy, out int sw, out int sh))
{
    // Pixels are BGRA8888 with straight (unpremultiplied) alpha, top row first.
    // Cache your framework's bitmap against the image object — a ConditionalWeakTable keyed on
    // `image` lets the bitmap die when the image does, with no eviction list to maintain.
    var bitmap = _bitmaps.GetOrCreate(image);

    // Edge tiles are clipped, so scale the destination to match rather than stretching a partial
    // tile over a whole cell.
    double destW = cellWidth  * sw / (double)image.CellWidth;
    double destH = cellHeight * sh / (double)image.CellHeight;

    DrawImage(bitmap,
        source: (sx, sy, sw, sh),
        dest: (col * cellWidth, row * cellHeight, destW, destH));
    continue;
}
```

Adjacent cells sharing the same `Image` reference and `ImageRow` with consecutive `ImageCol` values
are contiguous, so a renderer can coalesce them into a single draw call per row instead of one per
cell. If you cache rendered rows, note that image cells must break a text run: compare `Image` by
reference as well as comparing `Attributes`.

Image cells hold `" "` as their content, so `TranslateToString` and selection copy yield blanks.

**Options:**

| Option | Default | Purpose |
|---|---|---|
| `SixelEnabled` | `true` | Decode images, and advertise Sixel in the primary Device Attributes reply |
| `CellWidthPixels` / `CellHeightPixels` | `10` / `20` | Cell size images are laid out against |
| `MaxSixelPixels` | `4_000_000` | Largest single image accepted |
| `MaxImageBytes` | `64 MB` | Budget for image data live in the buffer; oldest are dropped past it |

Images are dropped when the terminal is resized to a different **column** count, because reflow
re-wraps lines by copying ranges of cells and the pieces would reassemble in the wrong places. A
change of row count alone keeps them.

## License

This project is licensed under the MIT License — see the [LICENSE](LICENSE) file for details.

## Author

Tom Laird-McConnell — [Iciclecreek](https://github.com/tomlm)

## Links

- [GitHub Repository](https://github.com/tomlm/XTerm.NET)
- [NuGet Package](https://www.nuget.org/packages/XTerm.NET)
