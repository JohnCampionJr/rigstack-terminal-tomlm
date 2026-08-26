using NeoSmart.Unicode;
using System.Text;
using Wcwidth;
using XTerm.Buffer;
using XTerm.Common;
using XTerm.Input;
using XTerm.Parser;

namespace XTerm;

/// <summary>
/// Handles input escape sequences and updates the terminal buffer.
/// Implements VT100/xterm escape sequence handlers.
/// </summary>
public class InputHandler
{
    private readonly Terminal _terminal;
    private Buffer.TerminalBuffer _buffer;
    private AttributeData _curAttr;
    private readonly Dictionary<CharsetMode, Dictionary<char, string>?> _charsets;
    private CharsetMode _currentCharset;

    // Variation selector and combining character constants
    private const int VariationSelectorEmojiSymbol = 0xFE0F;  // Emoji presentation selector
    private const int VariationSelectorTextSymbol = 0xFE0E;   // Text presentation selector
    private const int ZeroWidthJoiner = 0x200D;               // ZWJ for emoji sequences

    // Where a ZWJ was just merged, if anywhere. The character that FOLLOWS a ZWJ continues the same
    // grapheme cluster and belongs in the same cell, but it is an ordinary emoji and so passes no
    // combining-character test of its own — without this it opens a new cell and the cluster is spread
    // across the grid.
    //
    // A position rather than a flag, so it invalidates itself: anything that moves the cursor — an escape
    // sequence, a newline, a cursor address — leaves it pointing somewhere the next Print is not, and the
    // continuation is silently dropped rather than joining two unrelated characters.
    private (int Row, int Col)? _zwjContinuation;

    // Where a lone regional indicator is sitting, if one is. Two of them form one flag, and they arrive in
    // separate Print calls — so pairing them needs state that outlives a call, exactly as a ZWJ cluster does.
    //
    // Cell is where the first one went; Cursor is where it left the cursor. Both are checked, so a second
    // indicator pairs only when it lands exactly where the first one would have put it. Anything that moved
    // the cursor in between leaves two unrelated indicators standing alone, which is what they are.
    private (int Row, int Cell, int Cursor)? _regionalPending;

    /// <summary>The regional indicator symbols, U+1F1E6 to U+1F1FF. Two of them make one flag.</summary>
    private static bool IsRegionalIndicator(int codePoint)
        => codePoint >= 0x1F1E6 && codePoint <= 0x1F1FF;

    public InputHandler(Terminal terminal)
    {
        _terminal = terminal;
        _buffer = terminal.Buffer;
        _curAttr = AttributeData.Default;

        // Initialize charset tables - all start as ASCII
        _charsets = new Dictionary<CharsetMode, Dictionary<char, string>?>
        {
            { CharsetMode.G0, Charsets.ASCII },
            { CharsetMode.G1, Charsets.ASCII },
            { CharsetMode.G2, Charsets.ASCII },
            { CharsetMode.G3, Charsets.ASCII }
        };

        _currentCharset = CharsetMode.G0; // G0 is active by default
    }

    /// <summary>
    /// Checks if a code point is a combining character that should be merged with the previous cell.
    /// </summary>
    private static bool IsCombiningCharacter(int codePoint)
    {
        // Variation Selectors (U+FE00�U+FE0F)
        if (codePoint >= 0xFE00 && codePoint <= 0xFE0F)
            return true;

        // Variation Selectors Supplement (U+E0100�U+E01EF)
        if (codePoint >= 0xE0100 && codePoint <= 0xE01EF)
            return true;

        // Zero Width Joiner (U+200D)
        if (codePoint == ZeroWidthJoiner)
            return true;

        // Combining Diacritical Marks (U+0300�U+036F)
        if (codePoint >= 0x0300 && codePoint <= 0x036F)
            return true;

        // Combining Diacritical Marks Extended (U+1AB0�U+1AFF)
        if (codePoint >= 0x1AB0 && codePoint <= 0x1AFF)
            return true;

        // Combining Diacritical Marks Supplement (U+1DC0�U+1DFF)
        if (codePoint >= 0x1DC0 && codePoint <= 0x1DFF)
            return true;

        // Combining Diacritical Marks for Symbols (U+20D0�U+20FF)
        if (codePoint >= 0x20D0 && codePoint <= 0x20FF)
            return true;

        // Combining Half Marks (U+FE20�U+FE2F)
        if (codePoint >= 0xFE20 && codePoint <= 0xFE2F)
            return true;

        // Emoji Modifiers / Skin Tones (U+1F3FB..U+1F3FF)
        //
        // Combining is not decided here alone: a skin tone modifies an EMOJI, and TryAppendToPreviousCell
        // checks what it is being asked to attach to. Saying yes unconditionally glued a modifier onto
        // whatever happened to precede it — "║🏼║" put the tone inside the box-drawing character and drew
        // the pair as one unreadable cell, where every other terminal shows a swatch standing on its own.
        if (IsSkinToneModifier(codePoint))
            return true;

        // Keycap combining sequence (U+20E3)
        if (codePoint == 0x20E3)
            return true;

        return false;
    }

    /// <summary>The Fitzpatrick skin tone modifiers, U+1F3FB to U+1F3FF.</summary>
    private static bool IsSkinToneModifier(int codePoint)
        => codePoint >= 0x1F3FB && codePoint <= 0x1F3FF;

    /// <summary>
    /// The last code point in a cell's content — the one a modifier would actually be attaching to, since a
    /// cell may already hold a whole cluster.
    /// </summary>
    private static int LastRuneOf(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return 0;

        int last = 0;
        foreach (var rune in content.EnumerateRunes())
            last = rune.Value;

        return last;
    }

    /// <summary>
    /// Whether <paramref name="codePoint"/> is something a skin tone can actually modify.
    /// </summary>
    /// <remarks>
    /// Deliberately broader than Unicode's Emoji_Modifier_Base list, which runs to some thirty ranges and
    /// would have to be revised every release. Everything on it is an emoji, so "is this an emoji" rejects
    /// the case that matters — a letter, a box-drawing character, a CJK ideograph — while letting through a
    /// handful of emoji that take no modifier. Those render as whatever the font makes of them, which is
    /// what the program asked for; the alternative is a table that silently rots.
    /// </remarks>
    private static bool CanTakeSkinTone(int codePoint)
        => codePoint >= 0x1F000
           || codePoint == 0x261D || codePoint == 0x26F9
           || (codePoint >= 0x270A && codePoint <= 0x270D);

    /// <summary>
    /// Prints a character to the buffer.
    /// </summary>
    public void Print(string data)
    {
        // Check if this is a combining character that should be merged with the previous cell
        if (!string.IsNullOrEmpty(data))
        {
            var codePoint = char.ConvertToUtf32(data, 0);

            // A placeholder is a character that means "part of a picture goes here". It has to be
            // taken before the combining-character machinery below, which would otherwise try to
            // merge the diacritics that follow it into a text cell.
            if (codePoint == KittyPlaceholder && TryPrintKittyPlaceholder())
                return;

            // The combining marks that state a placeholder's tile explicitly. They must be taken
            // here too: left to the machinery below they would be appended to the image cell as
            // text, and left to nothing at all they would print as visible marks of their own.
            if (TryApplyPlaceholderDiacritic(codePoint))
                return;

            // A character standing exactly where a ZWJ was just merged continues that cluster.
            var continuesCluster = _zwjContinuation is { } pending
                                   && pending.Row == _buffer.Y + _buffer.YBase
                                   && pending.Col == _buffer.X;
            _zwjContinuation = null;

            // A second regional indicator lands beside the first and turns it into a flag: one glyph, two
            // columns. Handled here rather than through the combining path because it does not merely append
            // — the cell it joins has to GROW to the width the pair occupies, and gain the placeholder that
            // every wide cell carries.
            if (IsRegionalIndicator(codePoint)
                && _regionalPending is { } flag
                && flag.Row == _buffer.Y + _buffer.YBase
                && flag.Cursor == _buffer.X
                && TryPairRegionalIndicator(data, flag.Cell))
            {
                return;
            }

            // Any other character breaks the pair. A lone indicator is a perfectly good character — it
            // renders as a letter in a box — so it simply stops being the first half of anything.
            _regionalPending = null;

            if (continuesCluster || IsCombiningCharacter(codePoint))
            {
                // Find the previous cell to combine with
                if (TryAppendToPreviousCell(data, codePoint))
                {
                    // A ZWJ promises another component after it; remember where, so it can be recognised.
                    if (codePoint == ZeroWidthJoiner)
                        _zwjContinuation = (_buffer.Y + _buffer.YBase, _buffer.X);

                    return; // Successfully combined, don't create new cell
                }
                // If we can't combine (e.g., at start of line), fall through to normal handling
            }
        }


        // Handle autowrap
        if (_buffer.X >= _terminal.Cols)
        {
            if (_terminal.Options.Wraparound)
            {
                if (_buffer.Y == _buffer.ScrollBottom)
                {
                    _buffer.SetCursor(0, _buffer.Y);
                    _buffer.ScrollUp(1, true);
                }
                else
                {
                    _buffer.SetCursor(0, _buffer.Y + 1);
                }
                _buffer.Lines[_buffer.Y + _buffer.YBase]!.IsWrapped = true;
            }
            else
            {
                return; // Don't print beyond line edge
            }
        }

        var line = _buffer.Lines[_buffer.Y + _buffer.YBase]; 
        if (line == null)
            return;

        // Translate character through active charset
        var translatedData = data;
        if (data.Length == 1)
        {
            var charset = _charsets.GetValueOrDefault(_currentCharset);
            translatedData = Charsets.TranslateChar(data[0], charset);
        }

        // Get character width
        var width = GetStringCellWidth(translatedData);

        // Create cell
        var cell = new BufferCell
        {
            Content = translatedData,
            Width = width,
            Attributes = _curAttr,
            CodePoint = translatedData.Length > 0 ? char.ConvertToUtf32(translatedData, 0) : 0
        };

        // Insert mode handling
        if (_terminal.InsertMode)
        {
            // Shift cells right
            line?.CopyCellsFrom(line, _buffer.X, _buffer.X + width, _terminal.Cols - _buffer.X - width, false);
        }

        // A picture placed behind the text survives being typed over -- that is the whole meaning of
        // a negative z-index. Anything at zero or above is in front of the text and is replaced by
        // it, which is the ordinary case and the behaviour every image had before z existed.
        CarryBackgroundImage(line, _buffer.X, ref cell);

        // Set the cell
        line?.SetCell(_buffer.X, ref cell);

        // Handle wide characters
        if (width == 2)
        {
            // Set following cell as a spacer
            if (_buffer.X + 1 < _terminal.Cols)
            {
                var spacer = BufferCell.Empty;
                spacer.Attributes = _curAttr;
                CarryBackgroundImage(line, _buffer.X + 1, ref spacer);
                line?.SetCell(_buffer.X + 1, ref spacer);
            }
        }

        // A lone regional indicator may turn out to be the first half of a flag. Remember where it went and
        // where it left the cursor, so the next one can recognise itself as the second half.
        if (cell.CodePoint is var cp && IsRegionalIndicator(cp))
            _regionalPending = (_buffer.Y + _buffer.YBase, _buffer.X, _buffer.X + width);

        // Use MoveCursor to allow X to be one past the last column (pending wrap)
        _buffer.SetCursorRaw(_buffer.X + width, _buffer.Y);
    }

    /// <summary>
    /// Joins a second regional indicator to the one already at <paramref name="cellX"/>, making the pair a
    /// single double-width flag.
    /// </summary>
    /// <remarks>
    /// <para>The first indicator was printed as an ordinary single-width character, because at the time it
    /// was one — nothing says a flag is coming, and a lone indicator is a valid character that renders as a
    /// letter in a box. So this widens the cell it already wrote rather than laying a new one down.</para>
    /// <para>Returns false rather than half-doing it if the pair will not fit, which leaves the caller to
    /// print the second indicator on its own. Two boxed letters at the edge of the screen is a better answer
    /// than a wide cell hanging off it.</para>
    /// </remarks>
    private bool TryPairRegionalIndicator(string data, int cellX)
    {
        var line = _buffer.Lines[_buffer.Y + _buffer.YBase];
        if (line == null || cellX < 0 || cellX >= _terminal.Cols)
            return false;

        // The flag needs the column after it for the placeholder every wide cell carries.
        if (cellX + 1 >= _terminal.Cols)
            return false;

        var first = line[cellX];
        if (!IsRegionalIndicator(first.CodePoint))
            return false;

        // The first indicator already claimed both columns — one is two wide on its own, and the flag the
        // pair makes is the same two. So this joins the content and moves nothing: no new width, no second
        // placeholder, and the cursor stays where the first one left it.

        var flag = new BufferCell
        {
            Content = first.Content + data,
            Width = 2,
            Attributes = first.Attributes,
            // The FIRST indicator, which is what identifies the flag — and what a second call would test
            // against, if a third indicator ever arrives.
            CodePoint = first.CodePoint,
        };

        line.SetCell(cellX, ref flag);

        // A third indicator starts a new pair rather than joining this one, which is what UAX #29 says:
        // indicators pair up from the left, they do not accumulate.
        _regionalPending = null;
        return true;
    }

    /// <summary>
    /// Attempts to append a combining character to the previous cell.
    /// </summary>
    /// <param name="data">The combining character string.</param>
    /// <param name="codePoint">The code point of the combining character.</param>
    /// <returns>True if successfully combined, false otherwise.</returns>
    private bool TryAppendToPreviousCell(string data, int codePoint)
    {
        var line = _buffer.Lines[_buffer.Y + _buffer.YBase];
        if (line == null)
            return false;

        // Find the previous cell position
        int prevX = _buffer.X - 1;

        // If we're at the start of a line, we might need to look at the previous line
        if (prevX < 0)
        {
            // Check if the previous line exists and is wrapped
            if (_buffer.Y > 0 || _buffer.YBase > 0)
            {
                var prevLineIndex = _buffer.Y + _buffer.YBase - 1;
                if (prevLineIndex >= 0)
                {
                    var prevLine = _buffer.Lines[prevLineIndex];
                    if (prevLine != null && prevLine.IsWrapped)
                    {
                        line = prevLine;
                        prevX = _terminal.Cols - 1;
                    }
                    else
                    {
                        return false; // Can't combine at start of unwrapped line
                    }
                }
                else
                {
                    return false; // No previous line
                }
            }
            else
            {
                return false; // At the very beginning of the buffer
            }
        }

        // Get the previous cell
        if (prevX < 0 || prevX >= line.Length)
            return false;

        var prevCell = line[prevX];

        // A cell showing part of a picture has no text to combine with. Its content is a space kept
        // so that selecting the image copies blanks, and hanging an accent off it would put a mark
        // in the middle of the picture. Kitty's placeholders are followed by combining marks stating
        // a row and column, so this is the ordinary case rather than a curiosity.
        if (prevCell.IsImage)
            return false;

        // Skip placeholder cells (width 0) for wide characters - find the actual character cell
        while (prevX > 0 && prevCell.Width == 0)
        {
            prevX--;
            prevCell = line[prevX];
        }

        // Can't combine with empty cells
        if (prevCell.IsEmpty())
        {
            // Only allow combining with actual content, not empty/space cells
            // unless the space is the only content (which shouldn't happen for valid sequences)
            return false;
        }

        // A skin tone modifies an EMOJI. Attaching it to whatever happened to come first put the tone
        // inside a box-drawing character for "║🏼║" and drew the pair as one unreadable cell, where every
        // other terminal shows the swatch standing on its own. Refusing here sends it back to Print, which
        // gives it a cell of its own.
        if (IsSkinToneModifier(codePoint) && !CanTakeSkinTone(LastRuneOf(prevCell.Content)))
        {
            return false;
        }

        // Append the combining character to the previous cell's content
        var newContent = prevCell.Content + data;

        // Determine if we need to adjust the width
        int newWidth = prevCell.Width;

        // Handle variation selectors that change presentation
        if (codePoint == VariationSelectorEmojiSymbol && prevCell.Width == 1)
        {
            // Emoji presentation selector: character becomes width 2
            newWidth = 2;
        }
        else if (codePoint == VariationSelectorTextSymbol && prevCell.Width == 2)
        {
            // Text presentation selector: character becomes width 1
            newWidth = 1;
        }

        // Create the updated cell
        var updatedCell = new BufferCell
        {
            Content = newContent,
            Width = newWidth,
            Attributes = prevCell.Attributes,
            CodePoint = prevCell.CodePoint  // Keep the original base code point
        };

        line.SetCell(prevX, ref updatedCell);

        // Handle width changes
        if (newWidth != prevCell.Width)
        {
            if (newWidth == 2 && prevCell.Width == 1)
            {
                // Need to add a spacer cell after the character
                // Check if cursor position needs adjustment
                if (prevX + 1 < _terminal.Cols)
                {
                    // Use BufferCell.Spacer with the previous cell's attributes
                    var spacer = BufferCell.Empty;
                    spacer.Attributes = prevCell.Attributes;
                    line.SetCell(prevX + 1, ref spacer);

                    // Adjust cursor if we're after this cell
                    if (_buffer.X > prevX)
                    {
                        _buffer.SetCursorRaw(Math.Min(_buffer.X + 1, _terminal.Cols), _buffer.Y);
                    }
                }
            }
            else if (newWidth == 1 && prevCell.Width == 2)
            {
                // Remove the spacer cell by replacing with whitespace
                if (prevX + 1 < _terminal.Cols)
                {
                    // Use BufferCell.Whitespace with the previous cell's attributes
                    var emptyCell = BufferCell.Space;
                    emptyCell.Attributes = prevCell.Attributes;
                    line.SetCell(prevX + 1, ref emptyCell);

                    // Adjust cursor if we're after this cell
                    if (_buffer.X > prevX + 1)
                    {
                        _buffer.SetCursorRaw(Math.Max(_buffer.X - 1, 0), _buffer.Y);
                    }
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Handles CSI sequences (Control Sequence Introducer).
    /// </summary>
    public void HandleCsi(string identifier, Params parameters)
    {
        bool isPrivate = identifier.IsPrivateMode();
        var command = identifier.ToCsiCommand();

        switch (command)
        {
            case CsiCommand.InsertChars:
                InsertChars(parameters);
                break;

            case CsiCommand.CursorUp:
                CursorUp(parameters);
                break;

            case CsiCommand.CursorDown:
                CursorDown(parameters);
                break;

            case CsiCommand.CursorForward:
                CursorForward(parameters);
                break;

            case CsiCommand.CursorBackward:
                CursorBackward(parameters);
                break;

            case CsiCommand.CursorNextLine:
                CursorNextLine(parameters);
                break;

            case CsiCommand.CursorPreviousLine:
                CursorPrecedingLine(parameters);
                break;

            case CsiCommand.CursorCharAbsolute:
                CursorCharAbsolute(parameters);
                break;

            case CsiCommand.CursorPosition:
                CursorPosition(parameters);
                break;

            case CsiCommand.CursorForwardTab:
                CursorForwardTab(parameters);
                break;

            case CsiCommand.EraseInDisplay:
                EraseInDisplay(parameters);
                break;

            case CsiCommand.EraseInLine:
                EraseInLine(parameters);
                break;

            case CsiCommand.InsertLines:
                InsertLines(parameters);
                break;

            case CsiCommand.DeleteLines:
                DeleteLines(parameters);
                break;

            case CsiCommand.DeleteChars:
                DeleteChars(parameters);
                break;

            case CsiCommand.ScrollUp:
                // "CSI ? ... S" is XTSMGRAPHICS, not SCROLL UP. They share a final character, and
                // the identifier has its private marker stripped before the lookup, so without
                // this guard a Sixel program's opening capability query scrolled the screen.
                if (isPrivate)
                    GraphicsAttributes(parameters);
                else
                    ScrollUp(parameters);
                break;

            case CsiCommand.ScrollDown:
                ScrollDown(parameters);
                break;

            case CsiCommand.EraseChars:
                EraseChars(parameters);
                break;

            case CsiCommand.CursorBackwardTab:
                CursorBackwardTab(parameters);
                break;

            case CsiCommand.TabClear:
                TabClear(parameters);
                break;

            case CsiCommand.DeviceAttributes:
                DeviceAttributes(parameters, isPrivate);
                break;

            case CsiCommand.LinePositionAbsolute:
                LinePositionAbsolute(parameters);
                break;

            case CsiCommand.SelectGraphicRendition:
                CharAttributes(parameters);
                break;

            case CsiCommand.DeviceStatusReport:
                DeviceStatusReport(parameters, isPrivate);
                break;

            case CsiCommand.SetScrollRegion:
                SetScrollRegion(parameters);
                break;

            case CsiCommand.SaveCursorAnsi:
                SaveCursorAnsi();
                break;

            case CsiCommand.RestoreCursorAnsi:
                RestoreCursorAnsi();
                break;

            case CsiCommand.WindowManipulation:
                WindowManipulation(parameters);
                break;

            case CsiCommand.SelectCursorStyle:
                SelectCursorStyle(parameters);
                break;

            case CsiCommand.SetMode:
                SetCSIModeParameters(parameters, isPrivate: isPrivate);
                break;

            case CsiCommand.ResetMode:
                // DEC Private Mode Reset (CSI ? Pm l)
                ResetCSIModeParameters(parameters, isPrivate: isPrivate);
                break;

            case CsiCommand.Unknown:
                // Log unknown sequence for debugging
                System.Diagnostics.Debug.WriteLine($"Unknown CSI sequence: {identifier}");
                break;
        }
    }

    /// <summary>
    /// Handles ESC sequences.
    /// </summary>
    public void HandleEsc(string finalChar, string collected)
    {
        switch (finalChar)
        {
            case "D": // IND - Index
                IndexDown();
                break;
            case "E": // NEL - Next Line
                NextLine();
                break;
            case "M": // RI - Reverse Index
                ReverseIndex();
                break;
            case "c": // RIS - Reset to Initial State
                ResetTerminal();
                break;
            case "7": // DECSC - Save Cursor
                SaveCursor();
                break;
            case "8": // DECRC - Restore Cursor
                RestoreCursor();
                break;
        }

        // Charset designation sequences
        if (collected.Length > 0)
        {
            var intermediateChar = collected[0];
            switch (intermediateChar)
            {
                case '(': // Designate G0 character set
                    SetCharset(CharsetMode.G0, finalChar);
                    break;
                case ')': // Designate G1 character set
                    SetCharset(CharsetMode.G1, finalChar);
                    break;
                case '*': // Designate G2 character set
                    SetCharset(CharsetMode.G2, finalChar);
                    break;
                case '+': // Designate G3 character set
                    SetCharset(CharsetMode.G3, finalChar);
                    break;
                case '#': // DEC line attribute sequences
                    HandleDecLineAttribute(finalChar);
                    break;
            }
        }
    }

    private void HandleDecLineAttribute(string finalChar)
    {
        var line = _buffer.Lines[_buffer.Y + _buffer.YBase];
        if (line == null) return;
        switch (finalChar)
        {
            case "3": line.LineAttribute = LineAttribute.DoubleHeightTop; break;
            case "4": line.LineAttribute = LineAttribute.DoubleHeightBottom; break;
            case "5": line.LineAttribute = LineAttribute.Normal; break;
            case "6": line.LineAttribute = LineAttribute.DoubleWidth; break;
            case "8": FillScreenWithE(); break;
        }
    }

    private void FillScreenWithE()
    {
        var cell = new BufferCell('E', 1, AttributeData.Default);
        for (int row = 0; row < _terminal.Rows; row++)
        {
            var line = _buffer.Lines[_buffer.YBase + row];
            if (line != null)
            {
                line.LineAttribute = LineAttribute.Normal;
                line.Fill(cell);
            }
        }
        _buffer.SetCursor(0, 0);
    }

    private void SetCharset(CharsetMode mode, string charsetId)
    {
        var charset = Charsets.GetCharset(charsetId);
        _charsets[mode] = charset;
    }

    /// <summary>
    /// Shift Out - Select G1 character set (SO, 0x0E).
    /// </summary>
    public void ShiftOut()
    {
        _currentCharset = CharsetMode.G1;
    }

    /// <summary>
    /// Shift In - Select G0 character set (SI, 0x0F).
    /// </summary>
    public void ShiftIn()
    {
        _currentCharset = CharsetMode.G0;
    }

    /// <summary>
    /// Resets charset state to defaults.
    /// </summary>
    public void ResetCharsets()
    {
        _charsets[CharsetMode.G0] = Charsets.ASCII;
        _charsets[CharsetMode.G1] = Charsets.ASCII;
        _charsets[CharsetMode.G2] = Charsets.ASCII;
        _charsets[CharsetMode.G3] = Charsets.ASCII;
        _currentCharset = CharsetMode.G0;
    }

    #region DCS / Sixel

    /// <summary>The Sixel image being decoded, if a DECSIXEL payload is currently arriving.</summary>
    private Graphics.SixelDecoder? _sixelDecoder;

    /// <summary>
    /// The colour registers used when mode 1070 is reset, so images inherit each other's palette
    /// the way they did on a VT340. Built on first use, because the default is private registers
    /// and most sessions never touch this.
    /// </summary>
    private Graphics.SixelPalette? _sharedSixelPalette;

    /// <summary>
    /// Handles the start of a DCS sequence.
    /// </summary>
    /// <remarks>
    /// The payload that follows is streamed rather than handed over whole, so this is where we
    /// decide whether it is worth reading at all. Only DECSIXEL is; anything else is left to the
    /// parser's whole-payload event, which is capped and cheap.
    /// </remarks>
    public void HandleDcsHook(string identifier, Params parameters)
    {
        _sixelDecoder = null;

        if (identifier != "q" || !_terminal.Options.SixelEnabled)
            return;

        // P1 aspect ratio, P2 background select, P3 horizontal grid.
        var p1 = parameters.GetParam(0, 0);
        var p2 = parameters.GetParam(1, 0);
        var p3 = parameters.GetParam(2, 0);

        // Mode 1070 set -- the default -- gives every image its own registers, so one picture
        // cannot recolour the next. Reset shares one set across images.
        var palette = _terminal.SixelPrivateColorRegisters
            ? new Graphics.SixelPalette()
            : _sharedSixelPalette ??= new Graphics.SixelPalette();

        _sixelDecoder = new Graphics.SixelDecoder(
            p1, p2, p3,
            Math.Max(1, _terminal.Options.CellWidthPixels),
            Math.Max(1, _terminal.Options.CellHeightPixels),
            _terminal.Options.MaxSixelPixels,
            (uint)(0xFF000000 | (uint)(_terminal.Colors.Background & 0xFFFFFF)),
            palette);
    }

    /// <summary>
    /// Handles a chunk of a DCS payload.
    /// </summary>
    public void HandleDcsPut(ReadOnlySpan<char> data)
    {
        _sixelDecoder?.Put(data);
    }

    /// <summary>
    /// Handles the end of a DCS sequence.
    /// </summary>
    /// <param name="terminatedCleanly">
    /// False when the sequence was abandoned rather than terminated. A half-arrived image is
    /// dropped: showing the top third of a picture is not a kindness.
    /// </param>
    public void HandleDcsUnhook(bool terminatedCleanly)
    {
        var decoder = _sixelDecoder;
        _sixelDecoder = null;

        if (decoder is null || !terminatedCleanly)
            return;

        var image = decoder.Finish();
        if (image is not null)
            PlaceImage(Graphics.ImagePlacement.Natural(image));
    }

    /// <summary>The text of the APC sequence currently arriving.</summary>
    /// <remarks>
    /// One sequence at a time. A Kitty image spans several, and what carries across them is
    /// <see cref="_kittyTransmission"/>, not this.
    /// </remarks>
    private readonly StringBuilder _apcPayload = new();

    /// <summary>The image being assembled across several sequences, if one is.</summary>
    private Graphics.KittyTransmission? _kittyTransmission;

    /// <summary>Images the client has transmitted and may ask to see again.</summary>
    private readonly Graphics.ImageRegistry _kittyImages = new();

    /// <summary>
    /// Ceiling on the base64 held for one image, so a client that never sends its last chunk
    /// cannot make the terminal grow without limit.
    /// </summary>
    private int MaxKittyPayloadChars
    {
        get
        {
            // Enough base64 for the largest image allowed, plus slack for a PNG's own overhead.
            var bytes = (long)_terminal.Options.MaxSixelPixels * Graphics.TerminalImage.BytesPerPixel;
            var encoded = bytes * 4 / 3 + 1024;
            return (int)Math.Clamp(encoded, 4096, int.MaxValue);
        }
    }

    /// <summary>
    /// Handles the start of an APC sequence.
    /// </summary>
    /// <remarks>
    /// APC carries no parameters in front of its payload, so nothing can be decided here: what the
    /// sequence is depends on its first payload character, which has not arrived yet.
    /// </remarks>
    public void HandleApcHook(char introducer)
    {
        _ = introducer;
        _apcPayload.Clear();
    }

    /// <summary>
    /// Handles a chunk of an APC payload.
    /// </summary>
    public void HandleApcPut(ReadOnlySpan<char> data)
    {
        // Bounded here rather than at the end: the point is to stop a runaway sequence before the
        // memory is spent, not to notice afterwards.
        if (_apcPayload.Length <= MaxKittyPayloadChars)
            _apcPayload.Append(data);
    }

    /// <summary>
    /// Handles the end of an APC sequence.
    /// </summary>
    public void HandleApcUnhook(bool terminatedCleanly)
    {
        var payload = _apcPayload.ToString();
        _apcPayload.Clear();

        // A sequence cut short says nothing reliable about what it was carrying, and half a
        // transmission would corrupt whatever it was appended to.
        if (!terminatedCleanly)
        {
            _kittyTransmission = null;
            return;
        }

        if (payload.Length == 0 || payload[0] != 'G')
            return;
        if (!_terminal.Options.KittyGraphicsEnabled)
            return;

        HandleKittyGraphics(payload.AsSpan(1));
    }

    /// <summary>
    /// Keeps a behind-the-text picture on a cell that is being written over.
    /// </summary>
    /// <remarks>
    /// <para>A cell is rebuilt from scratch for every character printed, which is what gives images
    /// their overwrite semantics for free. A negative z-index is the one case where that is wrong:
    /// the client asked for the picture to sit BEHIND whatever text lands on it, so the placement
    /// has to be carried across to the new cell rather than dropped with the old one.</para>
    /// <para>Only negative z. At zero or above the picture is in front of the text and printing
    /// replaces it, exactly as it did before z-indexes were honoured at all.</para>
    /// </remarks>
    private static void CarryBackgroundImage(BufferLine? line, int col, ref BufferCell cell)
    {
        if (line is null || col < 0 || col >= line.Length)
            return;

        var existing = line[col];
        if (existing.Placement is not { } placement || placement.ZIndex >= 0)
            return;

        cell.Placement = placement;
        cell.ImageTile = existing.ImageTile;
    }

    /// <summary>
    /// U+10EEEE, the character Kitty uses to mean "part of a picture belongs in this cell".
    /// </summary>
    private const int KittyPlaceholder = 0x10EEEE;

    /// <summary>
    /// Where the placeholder rectangle currently being written started, so a cell can work out
    /// which tile of the picture it is.
    /// </summary>
    /// <remarks>
    /// The placement is held here as well as the position so that every cell of one run references
    /// the same object. A fresh placement per cell would be correct but would defeat the host's run
    /// coalescing, turning one blit per strip into one blit per cell.
    /// </remarks>
    private (int Row, int Col, uint ImageId, Graphics.ImagePlacement Placement)? _placeholderOrigin;

    /// <summary>
    /// The placeholder cell just written, and how many of its combining marks have arrived.
    /// </summary>
    /// <remarks>
    /// The marks modify the cell BEFORE them, and there are up to three: row, then column, then the
    /// most significant byte of the image id. Tracking the count is what tells the second from the
    /// first, since the characters themselves are drawn from one table and carry no clue which
    /// position they are filling.
    /// </remarks>
    private (int Row, int Col, int MarksSeen)? _placeholderCell;

    /// <summary>
    /// Writes a cell that a client marked as showing part of an image.
    /// </summary>
    /// <remarks>
    /// <para>The image is named by the cell's FOREGROUND COLOUR, which carries a 24-bit id rather
    /// than a colour. That works here because <c>AttributeData</c> keeps 25 bits for the value, so
    /// it survives the round trip unchanged.</para>
    /// <para>Which tile the cell shows is worked out from where it sits relative to the top-left of
    /// the run, which is how a contiguous rectangle written in reading order comes out right. A
    /// client may also state the row and column explicitly, as combining marks drawn from a fixed
    /// table; those arrive after this cell and are applied by
    /// <see cref="TryApplyPlaceholderDiacritic"/>.</para>
    /// </remarks>
    /// <returns>False when nothing can be resolved, so the character prints as ordinary text.</returns>
    private bool TryPrintKittyPlaceholder()
    {
        if (!_terminal.Options.KittyGraphicsEnabled)
            return false;

        // Mode 0 is a palette index; only a direct colour carries an id.
        if (_curAttr.GetFgColorMode() == 0)
            return false;

        var imageId = (uint)_curAttr.GetFgColor();
        if (imageId == 0 || !_kittyImages.TryGet(imageId, out var image))
            return false;

        var row = _buffer.Y + _buffer.YBase;
        var col = _buffer.X;

        // A cell continues the rectangle if it follows one -- along the same row, or at the start of
        // the row below. Anything else is a new picture starting here.
        var continues = _placeholderOrigin is { } origin
                        && origin.ImageId == imageId
                        && row >= origin.Row
                        && col >= origin.Col;

        if (!continues)
            _placeholderOrigin = (row, col, imageId, Graphics.ImagePlacement.Natural(image));

        var start = _placeholderOrigin!.Value;
        if (!WritePlaceholderCell(row, col, start.Placement, col - start.Col, row - start.Row))
            return false;

        _placeholderCell = (row, col, 0);
        _buffer.SetCursorRaw(_buffer.X + 1, _buffer.Y);
        return true;
    }

    /// <summary>Puts one tile of a placeholder run into a cell.</summary>
    /// <returns>False when the tile falls outside the picture.</returns>
    private bool WritePlaceholderCell(int row, int col, Graphics.ImagePlacement placement,
                                      int tileCol, int tileRow)
    {
        if (tileCol < 0 || tileRow < 0 || tileCol >= placement.Cols || tileRow >= placement.Rows)
            return false;

        var line = _buffer.Lines[row];
        if (line is null)
            return false;

        var cell = new BufferCell(" ", 1, _curAttr)
        {
            Placement = placement,
            ImageTile = BufferCell.PackTile(tileCol, tileRow)
        };
        line.SetCell(col, ref cell);
        return true;
    }

    /// <summary>
    /// Applies a combining mark that states part of the preceding placeholder cell's identity.
    /// </summary>
    /// <remarks>
    /// <para>The marks come in a fixed order and are positional: the first gives the tile row, the
    /// second the tile column, the third the most significant byte of the image id. A client may
    /// send fewer than three and let the rest be inferred, which is why each is applied on its own
    /// rather than waiting for the set.</para>
    /// <para>The third one can change WHICH image the cell shows, so the placement has to be
    /// rebuilt. That is rare -- it only matters for ids above 16777215 -- but resolving it late is
    /// the only option, since the id is not complete until the mark arrives.</para>
    /// </remarks>
    /// <returns>False if this is not a mark applying to a placeholder, so it prints normally.</returns>
    private bool TryApplyPlaceholderDiacritic(int codePoint)
    {
        if (_placeholderCell is not { } target || _placeholderOrigin is not { } origin)
            return false;

        // Only the cell immediately to the left, and only up to three marks.
        if (target.Row != _buffer.Y + _buffer.YBase || target.Col != _buffer.X - 1 || target.MarksSeen >= 3)
            return false;

        if (!Graphics.PlaceholderDiacritics.TryGetValue(codePoint, out var value))
            return false;

        var line = _buffer.Lines[target.Row];
        if (line is null)
            return false;

        var cell = line[target.Col];
        if (cell.Placement is null)
            return false;

        var tileCol = cell.ImageCol;
        var tileRow = cell.ImageRow;
        var placement = cell.Placement;

        switch (target.MarksSeen)
        {
            case 0:
                tileRow = value;
                break;

            case 1:
                tileCol = value;
                break;

            default:
                // The high byte of the id. Re-resolving can fail, and when it does the cell keeps
                // the picture it already had rather than becoming a blank.
                var extendedId = ((uint)value << 24) | (origin.ImageId & 0x00FFFFFF);
                if (_kittyImages.TryGet(extendedId, out var extended))
                {
                    placement = Graphics.ImagePlacement.Natural(extended);
                    _placeholderOrigin = (origin.Row, origin.Col, extendedId, placement);
                }
                break;
        }

        _placeholderCell = (target.Row, target.Col, target.MarksSeen + 1);

        // An explicit row or column outside the picture is a client error; keeping the cell as it
        // was is better than blanking it, and better than throwing on another process's input.
        if (tileCol >= placement.Cols || tileRow >= placement.Rows)
            return true;

        WritePlaceholderCell(target.Row, target.Col, placement, tileCol, tileRow);
        return true;
    }

    /// <summary>
    /// Handles one Kitty graphics command, payload and all.
    /// </summary>
    /// <remarks>
    /// The control data and the payload are separated by the first semicolon. A sequence may carry
    /// only control data and no semicolon at all -- which is exactly what the first chunk of a
    /// chunked transmission looks like.
    /// </remarks>
    private void HandleKittyGraphics(ReadOnlySpan<char> body)
    {
        var separator = body.IndexOf(';');
        var controlText = separator < 0 ? body : body[..separator];
        var payload = separator < 0 ? ReadOnlySpan<char>.Empty : body[(separator + 1)..];

        var command = Graphics.KittyCommand.Parse(controlText);

        // A continuation chunk carries only "m=", so the command it belongs to is the one held from
        // the first chunk. Without this, every chunk after the first would read as a fresh transmit.
        if (_kittyTransmission is not null)
        {
            _kittyTransmission.Append(payload);

            if (command.MoreChunks)
                return;

            var pending = _kittyTransmission;
            _kittyTransmission = null;
            CompleteKittyTransmission(pending);
            return;
        }

        switch (command.Action)
        {
            case Graphics.KittyAction.Transmit:
            case Graphics.KittyAction.TransmitAndDisplay:
            case Graphics.KittyAction.Query:
                BeginKittyTransmission(command, payload);
                break;

            case Graphics.KittyAction.Put:
                PlaceStoredKittyImage(command);
                break;

            case Graphics.KittyAction.Delete:
                DeleteKittyImages(command);
                break;

            default:
                // Animation, and anything else a later revision adds. Saying so is better than
                // silence: a client that asked can fall back rather than wait.
                ReplyToKitty(command, Graphics.KittyError.Unsupported);
                break;
        }
    }

    private void BeginKittyTransmission(Graphics.KittyCommand command, ReadOnlySpan<char> payload)
    {
        // Only the payload actually carried in the escape sequence. Reading a file the client names
        // would have the terminal open a path on its say-so, and this library runs inside hosts that
        // may hold more privilege than the program they are running.
        if (command.Medium != 'd')
        {
            ReplyToKitty(command, Graphics.KittyError.Unsupported);
            return;
        }

        // Refused on the declared size, before a byte of it is kept. A raw format states its
        // dimensions up front, so there is no reason to accumulate megabytes only to reject them --
        // and the payload cap would otherwise truncate the data and report it as corrupt instead of
        // as too large, which tells the client the wrong thing.
        if (command.Format != Graphics.KittyCommand.FormatPng
            && (long)command.Width * command.Height > _terminal.Options.MaxSixelPixels)
        {
            ReplyToKitty(command, Graphics.KittyError.TooLarge);
            return;
        }

        var transmission = new Graphics.KittyTransmission(command);
        transmission.Append(payload);

        if (command.MoreChunks)
        {
            _kittyTransmission = transmission;
            return;
        }

        CompleteKittyTransmission(transmission);
    }

    private void CompleteKittyTransmission(Graphics.KittyTransmission transmission)
    {
        var command = transmission.Command;

        var result = transmission.TryBuild(_terminal.Options.MaxSixelPixels,
                                           out var pixels, out var width, out var height);
        if (result != Graphics.KittyError.None)
        {
            ReplyToKitty(command, result);
            return;
        }

        var image = new Graphics.TerminalImage(
            pixels, width, height,
            Math.Max(1, _terminal.Options.CellWidthPixels),
            Math.Max(1, _terminal.Options.CellHeightPixels));

        // A query validates and answers. It must not put anything on the screen -- programs probe
        // with a real one-pixel image and expect their own output to be undisturbed.
        if (command.Action == Graphics.KittyAction.Query)
        {
            ReplyToKitty(command, Graphics.KittyError.None);
            return;
        }

        // A client that sent only a number gets an id chosen here, and is told what it was.
        var id = command.ImageId != 0 ? command.ImageId : _kittyImages.NextAssignedId();
        _kittyImages.Store(id, image, _terminal.Options.MaxImageRegistryBytes, command.ImageNumber);

        if (command.Action == Graphics.KittyAction.TransmitAndDisplay)
            PlaceKittyImage(image, command);

        ReplyToKitty(command, Graphics.KittyError.None, id);
    }

    private void PlaceStoredKittyImage(Graphics.KittyCommand command)
    {
        if (!TryResolveKittyImage(command, out var id, out var image))
        {
            ReplyToKitty(command, Graphics.KittyError.NotFound);
            return;
        }

        PlaceKittyImage(image, command);
        ReplyToKitty(command, Graphics.KittyError.None, id);
    }

    /// <summary>
    /// Finds a stored image from whichever identity the client used.
    /// </summary>
    /// <remarks>
    /// A client may name an image by the id it chose (<c>i=</c>) or by a number it chose
    /// (<c>I=</c>), leaving the terminal to pick the id. The id wins when both are present, since
    /// it is the more specific of the two.
    /// </remarks>
    private bool TryResolveKittyImage(Graphics.KittyCommand command,
                                      out uint id, out Graphics.TerminalImage image)
    {
        if (command.ImageId != 0)
        {
            id = command.ImageId;
            return _kittyImages.TryGet(id, out image);
        }

        if (command.ImageNumber != 0)
            return _kittyImages.TryGetByNumber(command.ImageNumber, out id, out image);

        id = 0;
        image = null!;
        return false;
    }

    /// <summary>
    /// Turns a Kitty display command into a placement and writes it into the buffer.
    /// </summary>
    private void PlaceKittyImage(Graphics.TerminalImage image, Graphics.KittyCommand command)
    {
        // A placeholder placement is shown by cells the client writes as text, not here.
        if (command.UnicodePlaceholder)
            return;

        var cropWidth = command.CropWidth > 0 ? command.CropWidth : image.PixelWidth - command.CropX;
        var cropHeight = command.CropHeight > 0 ? command.CropHeight : image.PixelHeight - command.CropY;
        if (cropWidth <= 0 || cropHeight <= 0)
            return;

        // c and r name a box to fill, which is a stretch. Without them the picture keeps its own
        // size and the edge tiles are clipped, which is a different calculation entirely.
        var stretched = command.Cols > 0 || command.Rows > 0;
        var cols = command.Cols > 0
            ? command.Cols
            : (cropWidth + image.CellWidth - 1) / image.CellWidth;
        var rows = command.Rows > 0
            ? command.Rows
            : (cropHeight + image.CellHeight - 1) / image.CellHeight;

        var placement = new Graphics.ImagePlacement(
            image, command.PlacementId,
            command.CropX, command.CropY, cropWidth, cropHeight,
            cols, rows,
            stretched ? Graphics.ImageScaling.Stretched : Graphics.ImageScaling.Natural,
            command.ZIndex, command.OffsetX, command.OffsetY);

        PlaceImage(placement, command.KeepCursor);
    }

    /// <summary>
    /// Removes placements, and with an upper-case target the pixels behind them too.
    /// </summary>
    /// <remarks>
    /// <para>The case of the target letter is the whole difference between "stop showing this" and
    /// "forget it entirely": lower case removes the appearances, upper case additionally releases
    /// the stored image so its id no longer resolves.</para>
    /// <para>Several keys mean something different here than they do on a transmission. On a delete,
    /// <c>x</c> and <c>y</c> are screen cell coordinates rather than a crop origin, and <c>z</c> is
    /// the z-index being matched rather than one being assigned. The protocol overloads them by
    /// action, so the parsed <c>CropX</c>/<c>CropY</c> carry the cell here.</para>
    /// <para>Positional targets find a placement through one of its cells and then remove all of it.
    /// Deleting only the cells that fall in the named row or column would leave a picture with a
    /// hole through it, which is not what "delete the placements intersecting row 3" means.</para>
    /// </remarks>
    private void DeleteKittyImages(Graphics.KittyCommand command)
    {
        var target = command.DeleteTarget;
        var alsoFree = char.IsUpper(target);

        // Kitty numbers the screen from one; the buffer numbers it from zero.
        var cellX = command.CropX - 1;
        var cellY = command.CropY - 1;

        switch (char.ToLowerInvariant(target))
        {
            case 'a':
                _terminal.Buffer.ClearImages();
                if (alsoFree)
                    _kittyImages.Clear();
                break;

            // By image id, or by image number -- d=i and d=n name different identities, so each
            // looks up the one it is about rather than sharing a resolver that prefers the id.
            case 'i':
            case 'n':
                DeleteKittyImageByIdentity(command, byNumber: char.ToLowerInvariant(target) == 'n',
                                           alsoFree);
                break;

            case 'c':
                DropPlacementsAt(_buffer.X, _buffer.Y, alsoFree);
                break;

            case 'p':
                DropPlacementsAt(cellX, cellY, alsoFree);
                break;

            case 'q':
                DropPlacementsWhere(p => p.ZIndex == command.ZIndex,
                                    (col, row) => col == cellX && row == cellY, alsoFree);
                break;

            case 'x':
                DropPlacementsWhere(null, (col, _) => col == cellX, alsoFree);
                break;

            case 'y':
                DropPlacementsWhere(null, (_, row) => row == cellY, alsoFree);
                break;

            case 'z':
                DropPlacementsWhere(p => p.ZIndex == command.ZIndex, null, alsoFree);
                break;

            case 'f':
                // Animation frames. Nothing here stores any, so there is nothing to remove -- but
                // saying "unsupported" would be wrong, since the requested state is the state.
                break;

            default:
                ReplyToKitty(command, Graphics.KittyError.Unsupported);
                return;
        }

        ReplyToKitty(command, Graphics.KittyError.None, command.ImageId);
    }

    /// <summary>
    /// Removes the appearances of one stored image, named by id or by number.
    /// </summary>
    /// <remarks>
    /// A placement id narrows it to a single appearance. That case deliberately does not release the
    /// pixels even for an upper-case target: other placements of the same image may still be on
    /// screen, and freeing it would blank pictures the client did not name.
    /// </remarks>
    private void DeleteKittyImageByIdentity(Graphics.KittyCommand command, bool byNumber, bool alsoFree)
    {
        uint id;
        Graphics.TerminalImage image;

        if (byNumber)
        {
            if (!_kittyImages.TryGetByNumber(command.ImageNumber, out id, out image))
                return;
        }
        else
        {
            id = command.ImageId;
            if (id == 0 || !_kittyImages.TryGet(id, out image))
                return;
        }

        if (command.PlacementId != 0)
        {
            _terminal.DropPlacements(p => ReferenceEquals(p.Image, image) && p.Id == command.PlacementId);
            return;
        }

        _terminal.DropImage(image);

        if (alsoFree)
            _kittyImages.Remove(id);
    }

    /// <summary>Removes every placement covering one screen cell.</summary>
    private void DropPlacementsAt(int col, int row, bool alsoFree)
        => DropPlacementsWhere(null, (c, r) => c == col && r == row, alsoFree);

    /// <summary>
    /// Removes placements chosen by identity, by position, or by both.
    /// </summary>
    /// <param name="matches">A test on the placement, or null to accept any.</param>
    /// <param name="cellMatches">A test on a cell's screen position, or null to search everywhere.</param>
    private void DropPlacementsWhere(Func<Graphics.ImagePlacement, bool>? matches,
                                     Func<int, int, bool>? cellMatches,
                                     bool alsoFree)
    {
        HashSet<Graphics.ImagePlacement> doomed;

        if (cellMatches is null)
        {
            // No position to search by, so every placement on screen is a candidate and the identity
            // test does all the work.
            doomed = _terminal.CollectPlacementsOnScreen((_, _) => true);
        }
        else
        {
            doomed = _terminal.CollectPlacementsOnScreen(cellMatches);
        }

        if (matches is not null)
            doomed.RemoveWhere(p => !matches(p));

        if (doomed.Count == 0)
            return;

        _terminal.DropPlacements(doomed);

        if (!alsoFree)
            return;

        // The images behind the placements that just went. Any of them still shown elsewhere is
        // kept, because releasing it would blank an appearance the client did not name.
        var stillShown = _terminal.CollectPlacementsOnScreen((_, _) => true);
        foreach (var placement in doomed)
        {
            if (!stillShown.Any(p => ReferenceEquals(p.Image, placement.Image)))
                _kittyImages.RemoveImage(placement.Image);
        }
    }

    /// <summary>
    /// Answers a Kitty command, unless the client asked not to be told.
    /// </summary>
    /// <remarks>
    /// q=1 suppresses success and q=2 suppresses failure as well. A reply is what a program uses to
    /// find out the terminal speaks this protocol at all, so silence is never the default.
    /// </remarks>
    private void ReplyToKitty(Graphics.KittyCommand command, Graphics.KittyError error, uint id = 0)
    {
        var succeeded = error == Graphics.KittyError.None;

        if (command.Quiet >= 2 || (command.Quiet >= 1 && succeeded))
            return;

        // An unsolicited reply to a command that named neither an id nor a number would be
        // unattributable, so the protocol asks for silence instead.
        var replyId = id != 0 ? id : command.ImageId;
        if (replyId == 0 && command.ImageNumber == 0)
            return;

        // A client that addressed the image by number needs both halves back: the number so it can
        // match the reply to the command it sent, and the id the terminal chose so it can use the
        // image afterwards. Only one of the two is known when the command failed early.
        var identity = (replyId, command.ImageNumber) switch
        {
            (0, var number) => $"I={number}",
            (var actual, 0) => $"i={actual}",
            (var actual, var number) => $"i={actual},I={number}"
        };
        var status = error switch
        {
            Graphics.KittyError.None => "OK",
            Graphics.KittyError.NotFound => "ENOENT:no such image",
            Graphics.KittyError.TooLarge => "EFBIG:image too large",
            Graphics.KittyError.Unsupported => "ENOTSUP:not supported",
            _ => "EINVAL:bad image data"
        };

        _terminal.RaiseDataReceived($"\u001b_G{identity};{status}\u001b\\");
    }

    /// <summary>
    /// Writes an image into the buffer, one cell per tile.
    /// </summary>
    /// <remarks>
    /// <para>Each covered cell gets a blank space carrying a reference to the shared image and the
    /// coordinates of the piece it shows. Writing it through <c>SetCell</c> is what makes the rest
    /// of the terminal treat it as content: the line's render cache is dropped, printing over it
    /// replaces it, erasing clears it, and scrolling carries it along.</para>
    /// <para>The cell keeps a space as its character so that selecting the image and copying it
    /// yields blanks rather than something unreadable.</para>
    /// </remarks>
    private void PlaceImage(Graphics.ImagePlacement placement, bool keepCursor = false)
    {
        // DECSDM set means the older display behaviour: pinned to the top-left, clipped rather
        // than scrolled, cursor untouched.
        var scrolling = !_terminal.SixelDisplayMode;

        var startCol = scrolling ? Math.Min(_buffer.X, _terminal.Cols - 1) : 0;
        if (startCol < 0)
            startCol = 0;
        var row = scrolling ? _buffer.Y : 0;

        var lastRowDrawn = row;

        for (int tileRow = 0; tileRow < placement.Rows; tileRow++)
        {
            if (row > _buffer.ScrollBottom)
            {
                if (!scrolling)
                    break; // clipped at the bottom of the screen

                // Ran off the bottom of the scroll region: push a line into the scrollback and
                // carry on writing at the last row, which is what a long image does to a screen.
                _buffer.ScrollUp(1);
                row = _buffer.ScrollBottom;
            }

            var line = _buffer.Lines[_buffer.YBase + row];
            if (line is null)
                break;

            for (int tileCol = 0; tileCol < placement.Cols; tileCol++)
            {
                var col = startCol + tileCol;
                if (col >= _terminal.Cols)
                    break;

                var existing = line[col];

                // A picture already here with a higher z-index stays in front. A cell holds one
                // placement, so "in front" has to mean "instead of" rather than "over the top of":
                // what is lost is blending two overlapping pictures, not the ordering between them.
                if (existing.Placement is { } inFront && inFront.ZIndex > placement.ZIndex)
                    continue;

                BufferCell cell;

                if (placement.ZIndex < 0 && !existing.IsImage)
                {
                    // Negative z means behind the text. The glyph and its attributes stay exactly as
                    // they were and the picture is attached underneath, so a host draws the tile and
                    // then the character over it -- which is how a background image is expressed.
                    cell = existing;
                }
                else
                {
                    cell = new BufferCell(" ", 1, _curAttr);
                }

                cell.Placement = placement;
                cell.ImageTile = BufferCell.PackTile(tileCol, tileRow);
                line.SetCell(col, ref cell);
            }

            lastRowDrawn = row;
            if (tileRow < placement.Rows - 1)
                row++;
        }

        if (!scrolling)
            return;

        // Kitty's C=1. The picture is drawn but the cursor does not follow it, which is what lets a
        // program place several images without tracking where each one left the caret.
        if (keepCursor)
        {
            _terminal.NoteImagePlaced(placement.Image);
            return;
        }

        if (_terminal.SixelCursorRight)
        {
            // Mode 8452: stay on the image's last row, just past its right edge.
            _buffer.SetCursor(Math.Min(startCol + placement.Cols, _terminal.Cols - 1), lastRowDrawn);
        }
        else
        {
            // The cursor belongs on the line below the image, which may need one more scroll if
            // the image finished on the last row of the region.
            var below = lastRowDrawn + 1;
            if (below > _buffer.ScrollBottom)
            {
                _buffer.ScrollUp(1);
                below = _buffer.ScrollBottom;
            }
            _buffer.SetCursor(0, below);
        }

        _terminal.NoteImagePlaced(placement.Image);
    }

    #endregion
    /// <summary>
    /// Handles OSC sequences (Operating System Command).
    /// </summary>
    public void HandleOsc(string data)
    {
        var parts = data.Split(new[] { ';' }, 2);
        if (parts.Length == 0)
            return;

        var arg = parts.Length > 1 ? parts[1] : string.Empty;

        // Whether this sequence reached a handler. Cleared by the branches that do nothing with it,
        // so a listener can tell "the terminal acted on this" from "the terminal saw it and moved on".
        var recognized = true;

        // Try to parse as OscCommand enum
        if (parts[0].TryParseOscCommand(out OscCommand command))
        {
            switch (command)
            {
                case OscCommand.SetIconAndTitle:
                case OscCommand.SetWindowTitle:
                    _terminal.Title = arg;
                    _terminal.RaiseTitleChanged(arg);
                    break;

                case OscCommand.SetIconName:
                    // Icon name - not typically supported in modern terminals
                    recognized = false;
                    break;

                case OscCommand.ChangeColor:
                    HandleColorPaletteChange(arg);
                    break;

                case OscCommand.CurrentDirectory:
                    HandleCurrentDirectory(arg);
                    break;

                case OscCommand.Hyperlink:
                    HandleHyperlink(arg);
                    break;

                case OscCommand.ConEmu:
                    HandleConEmu(arg);
                    break;

                case OscCommand.ShellIntegration:
                    HandleShellIntegration(arg);
                    break;

                case OscCommand.ForegroundColor:
                    HandleColorQuery(((int)command).ToString(), arg);
                    break;

                case OscCommand.BackgroundColor:
                    HandleColorQuery(((int)command).ToString(), arg);
                    break;

                case OscCommand.CursorColor:
                    HandleColorQuery(((int)command).ToString(), arg);
                    break;

                case OscCommand.Clipboard:
                    HandleClipboard(arg);
                    break;

                case OscCommand.ResetColor:
                case OscCommand.ResetForeground:
                case OscCommand.ResetBackground:
                case OscCommand.ResetCursor:
                    HandleColorReset(command, arg);
                    break;

                default:
                    // Known but unhandled command
                    recognized = false;
                    System.Diagnostics.Debug.WriteLine($"Unhandled OSC command: {command}");
                    break;
            }
        }
        else
        {
            // Unknown or unsupported OSC sequence
            recognized = false;
            System.Diagnostics.Debug.WriteLine($"Unknown OSC sequence: {parts[0]}");
        }

        // Last, so a listener observes the terminal's own handling as already done rather than
        // pending. Raised for recognized sequences too: a listener that only wants the rest can say
        // so with Recognized, and stop compensating by itself once a code lands here.
        _terminal.RaiseOscReceived(
            parts[0],
            int.TryParse(parts[0], out var code) ? code : -1,
            arg,
            data,
            recognized);
    }

    private void HandleColorPaletteChange(string data)
    {
        // OSC 4 ; index ; spec [ ; index ; spec ]... ST
        // Pairs, plural: xterm accepts any number in one sequence, and theme scripts routinely send
        // all sixteen ANSI colours at once rather than as sixteen sequences.
        var parts = data.Split(';');

        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            if (!int.TryParse(parts[i], out var index) || index < 0 || index >= ColorPalette.Size)
            {
                continue;
            }

            if (parts[i + 1] == "?")
            {
                // Answering with the CURRENT colour, not a constant. A program asking this is
                // usually about to pick its own colours to match.
                _terminal.RaiseDataReceived($"\u001b]4;{index};{ColorSpec.Format(_terminal.Colors[index])}\u0007");
                continue;
            }

            if (ColorSpec.TryParse(parts[i + 1], out var rgb))
            {
                _terminal.Colors.SetColor(index, rgb);
            }
        }
    }

    private void HandleCurrentDirectory(string data)
    {
        // OSC 7 ; file://hostname/path ST
        // Example: OSC 7;file://localhost/home/user ST
        if (data.StartsWith("file://"))
        {
            // Extract path from file:// URL
            var uri = data.Substring(7); // Remove "file://"
            var slashIndex = uri.IndexOf('/');
            if (slashIndex >= 0)
            {
                var path = uri.Substring(slashIndex);
                _terminal.CurrentDirectory = Uri.UnescapeDataString(path);
                _terminal.RaiseDirectoryChanged(_terminal.CurrentDirectory);
            }
        }
    }

    /// <summary>
    /// OSC 9 - ConEmu-style extensions, dispatched on the FIRST parameter rather than the code.
    /// </summary>
    private void HandleConEmu(string data)
    {
        // The sub-parameter decides which feature this is, and the notification form has no
        // sub-parameter at all -- OSC 9 ; text -- so it can only be the fallback. That makes the
        // ORDER load-bearing rather than incidental: every claimed sub-command has to be matched
        // first, or OSC 9;4;1;50 pops a toast reading "4;1;50" on every progress tick.
        //
        // An unclaimed sub-parameter is therefore a notification by definition, which is the right
        // reading of a permissive extension space, and means a future ConEmu code shows up as text
        // rather than being dropped.
        var parts = data.Split(new[] { ';' }, 2);

        if (parts.Length == 1 && (data == "9" || data == "4"))
        {
            // A claimed sub-command with nothing after it. Malformed rather than a notification:
            // reporting it as one would raise a toast whose entire body is "9".
            return;
        }

        if (parts.Length == 2 && parts[0] == "9")
        {
            // OSC 9 ; 9 ; path ST - working directory, the ConEmu convention. Microsoft's documented
            // Windows prompts emit THIS rather than OSC 7, so a terminal that only reads 7 silently
            // loses the cwd on Windows. Path is bare, not a file:// URI, and pwsh quotes it.
            var path = parts[1].Trim('"');
            if (!string.IsNullOrEmpty(path))
            {
                _terminal.CurrentDirectory = path;
                _terminal.RaiseDirectoryChanged(path);
            }

            return;
        }

        if (parts.Length == 2 && parts[0] == "4")
        {
            HandleProgress(parts[1]);
            return;
        }

        // OSC 9 ; text ST - desktop notification (the iTerm2 reading of this code).
        if (!string.IsNullOrEmpty(data))
        {
            _terminal.RaiseNotificationReceived(data);
        }
    }

    /// <summary>
    /// OSC 9 ; 4 ; state ; progress ST - progress reporting.
    /// </summary>
    private void HandleProgress(string data)
    {
        var parts = data.Split(';');

        if (!int.TryParse(parts[0], out var rawState) || !Enum.IsDefined(typeof(ProgressState), rawState))
        {
            return;
        }

        var state = (ProgressState)rawState;

        // Value is absent for None and Indeterminate, and meaningless anyway; clamped rather than
        // rejected, because a sender that overshoots still means "as far as it goes".
        var value = 0;
        if (parts.Length > 1 && int.TryParse(parts[1], out var parsed))
        {
            value = Math.Clamp(parsed, 0, 100);
        }

        if (state == ProgressState.None || state == ProgressState.Indeterminate)
        {
            value = 0;
        }

        _terminal.ProgressState = state;
        _terminal.ProgressValue = value;
        _terminal.RaiseProgressChanged(state, value);
    }

    /// <summary>
    /// OSC 133 - FinalTerm/FTCS shell integration marks.
    /// </summary>
    private void HandleShellIntegration(string data)
    {
        var parts = data.Split(';');
        if (parts.Length == 0 || parts[0].Length == 0)
        {
            return;
        }

        ShellIntegrationMark mark;
        switch (parts[0])
        {
            case "A": mark = ShellIntegrationMark.PromptStart; break;
            case "B": mark = ShellIntegrationMark.CommandStart; break;
            case "C": mark = ShellIntegrationMark.CommandExecuted; break;
            case "D": mark = ShellIntegrationMark.CommandFinished; break;
            default: return;
        }

        int? exitCode = null;
        if (mark == ShellIntegrationMark.CommandFinished)
        {
            // Only D carries one, and it is optional even there: cmd.exe cannot read the previous
            // command's status from its prompt and always sends a bare D. Left null rather than
            // defaulted to 0, so "not reported" never reads as "succeeded".
            if (parts.Length > 1 && int.TryParse(parts[1], out var parsedExit))
            {
                exitCode = parsedExit;
            }

            _terminal.LastCommandExitCode = exitCode;
        }

        _terminal.ShellIntegrationState = mark;
        _terminal.RaiseShellIntegrationMark(mark, exitCode);
    }

    private void HandleHyperlink(string data)
    {
        // OSC 8 ; params ; URI ST
        // Example: OSC 8;;http://example.com ST (start link)
        //          OSC 8;; ST (end link)
        var parts = data.Split(new[] { ';' }, 2);

        if (parts.Length >= 2)
        {
            var params_ = parts[0];
            var uri = parts[1];

            if (string.IsNullOrEmpty(uri))
            {
                // End hyperlink
                _terminal.CurrentHyperlink = null;
                _terminal.HyperlinkId = null;
                _terminal.RaiseHyperlinkChanged(null);
            }
            else
            {
                // Start hyperlink
                _terminal.CurrentHyperlink = uri;

                // Parse params for id= parameter
                if (!string.IsNullOrEmpty(params_))
                {
                    var paramParts = params_.Split(':');
                    foreach (var p in paramParts)
                    {
                        if (p.StartsWith("id="))
                        {
                            _terminal.HyperlinkId = p.Substring(3);
                        }
                    }
                }

                _terminal.RaiseHyperlinkChanged(uri);
            }
        }
    }

    private void HandleColorQuery(string colorType, string data)
    {
        // OSC 10/11/12 ; spec [ ; spec ]... ST  - set, or query when spec is "?"
        //
        // Multiple specs advance through the resources in order, so OSC 10 ; fg ; bg sets the
        // foreground AND the background. xterm defines it that way and shell prompts written for
        // xterm use it, so handling only the first would set the foreground and silently drop the
        // background.
        if (!int.TryParse(colorType, out var resource))
        {
            return;
        }

        foreach (var spec in data.Split(';'))
        {
            if (resource > 12)
            {
                break;
            }

            if (spec == "?")
            {
                var current = resource switch
                {
                    10 => _terminal.Colors.Foreground,
                    11 => _terminal.Colors.Background,
                    _ => _terminal.Colors.Cursor,
                };

                // The real colour, not a constant. Programs query OSC 11 to decide whether they are
                // on a light or a dark terminal; answering black regardless told every one of them
                // "dark", and a light theme got dark-theme colours drawn onto it.
                _terminal.RaiseDataReceived($"\u001b]{resource};{ColorSpec.Format(current)}\u0007");
            }
            else if (ColorSpec.TryParse(spec, out var rgb))
            {
                switch (resource)
                {
                    case 10: _terminal.Colors.SetForeground(rgb); break;
                    case 11: _terminal.Colors.SetBackground(rgb); break;
                    case 12: _terminal.Colors.SetCursor(rgb); break;
                }
            }

            resource++;
        }
    }

    private void HandleClipboard(string data)
    {
        // OSC 52 ; c ; data ST
        // Example: OSC 52;c;base64data ST
        var parts = data.Split(new[] { ';' }, 2);

        if (parts.Length >= 2)
        {
            var target = parts[0]; // Usually 'c' for clipboard, 'p' for primary
            var clipdata = parts[1];

            if (clipdata == "?")
            {
                // Query clipboard - respond with clipboard content
                // Format: OSC 52 ; c ; base64data ST
                // For security, many terminals don't support this
                // We'll send an empty response
                _terminal.RaiseDataReceived($"\u001b]52;{target};\u0007");
            }
            else
            {
                // Set clipboard
                try
                {
                    var decoded = Convert.FromBase64String(clipdata);
                    var text = System.Text.Encoding.UTF8.GetString(decoded);
                    // TODO: Integrate with system clipboard
                    // For now, we just acknowledge receipt
                }
                catch
                {
                    // Invalid base64 or encoding
                }
            }
        }
    }

    private void HandleColorReset(OscCommand command, string data)
    {
        // OSC 104 [ ; index ]... ST  - reset palette entries, or all of them when bare
        // OSC 110/111/112 ST         - reset foreground / background / cursor
        //
        // "Reset" means back to the EMBEDDER'S theme, not to a factory dark palette. Anything else
        // and a program calling OSC 104 would drag a light terminal to black and leave it there.
        switch (command)
        {
            case OscCommand.ResetForeground:
                _terminal.Colors.ResetForeground();
                return;

            case OscCommand.ResetBackground:
                _terminal.Colors.ResetBackground();
                return;

            case OscCommand.ResetCursor:
                _terminal.Colors.ResetCursor();
                return;
        }

        if (string.IsNullOrEmpty(data))
        {
            _terminal.Colors.ResetAllColors();
            return;
        }

        foreach (var part in data.Split(';'))
        {
            if (int.TryParse(part, out var index) && index >= 0 && index < ColorPalette.Size)
            {
                _terminal.Colors.ResetColor(index);
            }
        }
    }

    // CSI Handler Implementations

    private void CursorUp(Params parameters)
    {
        var count = Math.Max(parameters.GetParam(0, 1), 1);
        _buffer.SetCursor(_buffer.X, Math.Max(_buffer.Y - count, 0));
    }

    private void CursorDown(Params parameters)
    {
        var count = Math.Max(parameters.GetParam(0, 1), 1);
        _buffer.SetCursor(_buffer.X, Math.Min(_buffer.Y + count, _terminal.Rows - 1));
    }

    private void CursorForward(Params parameters)
    {
        var count = Math.Max(parameters.GetParam(0, 1), 1);
        _buffer.SetCursor(Math.Min(_buffer.X + count, _terminal.Cols - 1), _buffer.Y);
    }

    private void CursorBackward(Params parameters)
    {
        var count = Math.Max(parameters.GetParam(0, 1), 1);
        _buffer.SetCursor(Math.Max(_buffer.X - count, 0), _buffer.Y);
    }

    private void CursorNextLine(Params parameters)
    {
        var count = Math.Max(parameters.GetParam(0, 1), 1);
        _buffer.SetCursor(0, Math.Min(_buffer.Y + count, _terminal.Rows - 1));
    }

    private void CursorPrecedingLine(Params parameters)
    {
        var count = Math.Max(parameters.GetParam(0, 1), 1);
        _buffer.SetCursor(0, Math.Max(_buffer.Y - count, 0));
    }

    private void CursorCharAbsolute(Params parameters)
    {
        var col = Math.Max(parameters.GetParam(0, 1), 1) - 1;
        _buffer.SetCursor(col, _buffer.Y);
    }

    private void CursorPosition(Params parameters)
    {
        var row = Math.Max(parameters.GetParam(0, 1), 1) - 1;
        var col = Math.Max(parameters.GetParam(1, 1), 1) - 1;
        row = GetAbsoluteCursorRow(row);
        _buffer.SetCursor(col, row);
    }

    private void EraseInDisplay(Params parameters)
    {
        var mode = parameters.GetParam(0, 0);
        var emptyCell = BufferCell.Space;
        emptyCell.Attributes = _curAttr;

        switch (mode)
        {
            case 0: // Erase below
                EraseInLine(parameters); // Current line from cursor
                for (int i = _buffer.Y + 1; i < _terminal.Rows; i++)
                {
                    _buffer.Lines[_buffer.YBase + i]?.Fill(emptyCell);
                }
                break;
            case 1: // Erase above
                for (int i = 0; i < _buffer.Y; i++)
                {
                    _buffer.Lines[_buffer.YBase + i]?.Fill(emptyCell);
                }
                EraseInLine(parameters); // Current line to cursor
                break;
            case 2: // Erase all — the visible screen only; the scrollback is kept
                for (int i = 0; i < _terminal.Rows; i++)
                {
                    _buffer.Lines[_buffer.YBase + i]?.Fill(emptyCell);
                }
                break;
            case 3: // Erase scrollback (xterm extension) — the scrollback only; the screen is kept
                // Previously shared the body above, which erases the VISIBLE screen and never touches the
                // scrollback: the opposite of what mode 3 asks for. The two modes are complements, not
                // variations, so a caller wanting both sends 2 and 3 — which is exactly what cmd.exe's
                // `cls` does under ConPTY (it clears the screen line by line, then sends CSI 3 J).
                //
                // Discarding rather than blanking is the point: blanked lines are still scrollable, so the
                // history stayed reachable with the mouse wheel even though the terminal had been told to
                // throw it away.
                _buffer.ClearScrollback();
                break;
        }
    }

    private void EraseInLine(Params parameters)
    {
        var mode = parameters.GetParam(0, 0);
        var line = _buffer.Lines[_buffer.Y + _buffer.YBase];
        if (line == null)
            return;

        var emptyCell = BufferCell.Space;
        emptyCell.Attributes = _curAttr;

        switch (mode)
        {
            case 0: // Erase to right
                line.Fill(emptyCell, _buffer.X, _terminal.Cols);
                break;
            case 1: // Erase to left
                line.Fill(emptyCell, 0, _buffer.X + 1);
                break;
            case 2: // Erase entire line
                line.Fill(emptyCell);
                break;
        }
    }

    private void InsertLines(Params parameters)
    {
        var count = Math.Max(parameters.GetParam(0, 1), 1);
        // Only works in scroll region
        if (_buffer.Y < _buffer.ScrollTop || _buffer.Y > _buffer.ScrollBottom)
            return;

        for (int i = 0; i < count; i++)
        {
            _buffer.Lines.Splice(_buffer.YBase + _buffer.ScrollBottom, 1);
            _buffer.Lines.Splice(_buffer.Y + _buffer.YBase, 0,
                _buffer.GetBlankLine(_curAttr));
        }
    }

    private void DeleteLines(Params parameters)
    {
        var count = Math.Max(parameters.GetParam(0, 1), 1);
        // Only works in scroll region
        if (_buffer.Y < _buffer.ScrollTop || _buffer.Y > _buffer.ScrollBottom)
            return;

        for (int i = 0; i < count; i++)
        {
            _buffer.Lines.Splice(_buffer.Y + _buffer.YBase, 1);
            _buffer.Lines.Splice(_buffer.YBase + _buffer.ScrollBottom, 0,
                _buffer.GetBlankLine(_curAttr));
        }
    }

    private void InsertChars(Params parameters)
    {
        var count = Math.Max(parameters.GetParam(0, 1), 1);
        var line = _buffer.Lines[_buffer.Y + _buffer.YBase];
        if (line == null)
            return;

        // Shift cells right from cursor position
        line.CopyCellsFrom(line, _buffer.X, _buffer.X + count,
            _terminal.Cols - _buffer.X - count, false);

        // Blank the inserted cells at cursor position
        var emptyCell = BufferCell.Space;
        emptyCell.Attributes = _curAttr;
        line.Fill(emptyCell, _buffer.X, Math.Min(_buffer.X + count, _terminal.Cols));
    }

    private void DeleteChars(Params parameters)
    {
        var count = Math.Max(parameters.GetParam(0, 1), 1);
        var line = _buffer.Lines[_buffer.Y + _buffer.YBase];
        if (line == null)
            return;

        // Limit count to remaining characters on line
        var remaining = _terminal.Cols - _buffer.X;
        count = Math.Min(count, remaining);

        line.CopyCellsFrom(line, _buffer.X + count, _buffer.X,
            _terminal.Cols - _buffer.X - count, false);

        // Fill vacated cells at right edge with current attributes (BCE)
        var emptyCell = BufferCell.Space;
        emptyCell.Attributes = _curAttr;
        line.Fill(emptyCell, _terminal.Cols - count, _terminal.Cols);
    }

    private void EraseChars(Params parameters)
    {
        var count = Math.Max(parameters.GetParam(0, 1), 1);
        var line = _buffer.Lines[_buffer.Y + _buffer.YBase];

        var emptyCell = BufferCell.Space;
        emptyCell.Attributes = _curAttr;

        line?.Fill(emptyCell, _buffer.X, Math.Min(_buffer.X + count, _terminal.Cols));
    }

    private void ScrollUp(Params parameters)
    {
        var count = Math.Max(parameters.GetParam(0, 1), 1);
        _buffer.ScrollUp(count);
    }

    private void ScrollDown(Params parameters)
    {
        var count = Math.Max(parameters.GetParam(0, 1), 1);
        _buffer.ScrollDown(count);
    }

    private void SaveCursorAnsi()
    {
        // ANSI save cursor (CSI s) - same as DEC DECSC but simpler
        SaveCursor();
    }

    private void RestoreCursorAnsi()
    {
        // ANSI restore cursor (CSI u) - same as DEC DECRC but simpler
        RestoreCursor();
    }

    private void LinePositionAbsolute(Params parameters)
    {
        // VPA - Line Position Absolute (CSI d)
        var row = Math.Max(parameters.GetParam(0, 1), 1) - 1;
        row = GetAbsoluteCursorRow(row);
        _buffer.SetCursor(_buffer.X, row);
    }

    private void CursorForwardTab(Params parameters)
    {
        // CHT - Cursor Forward Tabulation (CSI I)
        var count = Math.Max(parameters.GetParam(0, 1), 1);
        var tabWidth = _terminal.Options.TabStopWidth;

        for (int i = 0; i < count; i++)
        {
            var nextTabStop = ((_buffer.X / tabWidth) + 1) * tabWidth;
            _buffer.SetCursor(Math.Min(nextTabStop, _terminal.Cols - 1), _buffer.Y);
        }
    }

    private void CursorBackwardTab(Params parameters)
    {
        // CBT - Cursor Backward Tabulation (CSI Z)
        var count = Math.Max(parameters.GetParam(0, 1), 1);
        var tabWidth = _terminal.Options.TabStopWidth;

        for (int i = 0; i < count; i++)
        {
            if (_buffer.X == 0)
                break;

            var prevTabStop = ((_buffer.X - 1) / tabWidth) * tabWidth;
            _buffer.SetCursor(Math.Max(prevTabStop, 0), _buffer.Y);
        }
    }

    private void TabClear(Params parameters)
    {
        // TBC - Tab Clear (CSI g)
        // Ps = 0: Clear tab stop at current column (default)
        // Ps = 3: Clear all tab stops
        // Note: We use fixed tab stops, so this is acknowledged but has no effect
        // A full implementation would maintain a list of custom tab stops
        var mode = parameters.GetParam(0, 0);
        switch (mode)
        {
            case 0:
                // Clear current column tab stop - acknowledged but no action
                break;
            case 3:
                // Clear all tab stops - acknowledged but no action
                break;
        }
    }

    private void DeviceAttributes(Params parameters, bool isPrivate)
    {
        // DA - Device Attributes (CSI c or CSI > c)
        if (isPrivate)
        {
            // Secondary DA (CSI > c) - Report terminal ID and version
            // Response: CSI > 0 ; version ; 0 c
            // We report as VT100-compatible
            _terminal.RaiseDataReceived("\u001b[>0;10;0c");
        }
        else
        {
            // Primary DA (CSI c) - Report device attributes
            // Response: CSI ? 1 ; 2 c (VT100 with AVO)
            // More complete: CSI ? 1 ; 2 ; 6 ; 9 c
            // 1 = 132 columns, 2 = Printer, 6 = Selective erase, 9 = National replacement character sets
            //
            // Attribute 4 is Sixel graphics, and it is not decoration: libsixel, chafa, img2sixel
            // and everything built on them read this reply, and send text art instead of pictures
            // unless they see it. Claiming it while Sixel is switched off would be a lie in the
            // other direction, so it follows the option.
            _terminal.RaiseDataReceived(_terminal.Options.SixelEnabled
                ? "\u001b[?1;2;4c"
                : "\u001b[?1;2c");
        }
    }

    /// <summary>
    /// XTSMGRAPHICS -- CSI ? Pi ; Pa ; Pv S. Reports the terminal's graphics limits.
    /// </summary>
    /// <remarks>
    /// <para>This shares its final character with SCROLL UP, and <c>ToCsiCommand</c> strips the
    /// private marker before looking the command up, so until this existed a graphics query
    /// scrolled the screen instead of being answered. Every Sixel-capable program sends one during
    /// startup, which made the damage routine rather than obscure.</para>
    /// <para>Only the read operations are answered. The limits are fixed, so accepting a request
    /// to change them and quietly not doing it would be worse than refusing outright.</para>
    /// </remarks>
    private void GraphicsAttributes(Params parameters)
    {
        const int readAttribute = 1;
        const int readDefault = 2;
        const int readMaximum = 4;

        const int success = 0;
        const int badItem = 1;
        const int badAction = 2;

        var item = parameters.GetParam(0, 0);
        var action = parameters.GetParam(1, 0);
        var isRead = action == readAttribute || action == readDefault || action == readMaximum;

        switch (item)
        {
            case 1: // number of colour registers
                _terminal.RaiseDataReceived(isRead
                    ? $"\u001b[?1;{success};{Graphics.SixelPalette.RegisterCount}S"
                    : $"\u001b[?1;{badAction}S");
                break;

            case 2: // Sixel geometry
                if (isRead)
                {
                    // Reported as what MaxSixelPixels allows across the full terminal width, so a
                    // program that sizes an image to fit gets one we will not then throw away.
                    var width = Math.Max(1, _terminal.Cols * Math.Max(1, _terminal.Options.CellWidthPixels));
                    var height = Math.Max(1, _terminal.Options.MaxSixelPixels / width);
                    _terminal.RaiseDataReceived($"\u001b[?2;{success};{width};{height}S");
                }
                else
                {
                    _terminal.RaiseDataReceived($"\u001b[?2;{badAction}S");
                }
                break;

            default:
                _terminal.RaiseDataReceived($"\u001b[?{item};{badItem}S");
                break;
        }
    }
    private void DeviceStatusReport(Params parameters, bool isPrivate)
    {
        // DSR - Device Status Report (CSI n or CSI ? n)
        var report = parameters.GetParam(0, 0);

        if (isPrivate)
        {
            // DEC-specific DSR
            switch (report)
            {
                case 6: // DECXCPR - Extended Cursor Position Report
                    // Report cursor position: CSI ? row ; col R
                    var row = _buffer.Y + 1; // 1-based
                    var col = _buffer.X + 1; // 1-based
                    _terminal.RaiseDataReceived($"\u001b[?{row};{col}R");
                    break;

                case 15: // Printer status
                    // Report no printer: CSI ? 1 3 n
                    _terminal.RaiseDataReceived("\u001b[?13n");
                    break;

                case 25: // UDK status
                    // Report UDK locked: CSI ? 2 1 n
                    _terminal.RaiseDataReceived("\u001b[?21n");
                    break;

                case 26: // Keyboard status
                    // Report keyboard ready: CSI ? 2 7 ; 1 ; 0 ; 0 n
                    _terminal.RaiseDataReceived("\u001b[?27;1;0;0n");
                    break;
            }
        }
        else
        {
            // Standard ANSI DSR
            switch (report)
            {
                case 5: // Operating status
                    // Report OK: CSI 0 n
                    _terminal.RaiseDataReceived("\u001b[0n");
                    break;

                case 6: // CPR - Cursor Position Report
                    // Report cursor position: CSI row ; col R
                    var row = _buffer.Y + 1; // 1-based
                    var col = _buffer.X + 1; // 1-based

                    // Adjust for origin mode
                    if (_terminal.OriginMode)
                    {
                        row = row - _buffer.ScrollTop;
                    }

                    _terminal.RaiseDataReceived($"\u001b[{row};{col}R");
                    break;
            }
        }
    }

    private void CharAttributes(Params parameters)
    {
        if (parameters.Length == 0)
        {
            _curAttr = AttributeData.Default;
            return;
        }

        for (int i = 0; i < parameters.Length; i++)
        {
            var param = parameters.GetParam(i, 0);

            switch (param)
            {
                case 0: // Reset
                    _curAttr = AttributeData.Default;
                    break;
                case 1: // Bold
                    _curAttr.SetBold(true);
                    break;
                case 2: // Dim
                    _curAttr.SetDim(true);
                    break;
                case 3: // Italic
                    _curAttr.SetItalic(true);
                    break;
                case 4: // Underline
                    _curAttr.SetUnderline(true);
                    break;
                case 5: // Blink
                    _curAttr.SetBlink(true);
                    break;
                case 7: // Inverse
                    _curAttr.SetInverse(true);
                    break;
                case 8: // Invisible
                    _curAttr.SetInvisible(true);
                    break;
                case 9: // Strikethrough
                    _curAttr.SetStrikethrough(true);
                    break;
                case 22: // Not bold/dim
                    _curAttr.SetBold(false);
                    _curAttr.SetDim(false);
                    break;
                case 23: // Not italic
                    _curAttr.SetItalic(false);
                    break;
                case 24: // Not underline
                    _curAttr.SetUnderline(false);
                    break;
                case 25: // Not blink
                    _curAttr.SetBlink(false);
                    break;
                case 27: // Not inverse
                    _curAttr.SetInverse(false);
                    break;
                case 28: // Not invisible
                    _curAttr.SetInvisible(false);
                    break;
                case 29: // Not strikethrough
                    _curAttr.SetStrikethrough(false);
                    break;
                case >= 30 and <= 37: // Foreground color
                    _curAttr.SetFgColor(param - 30);
                    break;
                case 38: // Extended foreground color
                    i = HandleExtendedColor(parameters, i, true);
                    break;
                case 39: // Default foreground
                    _curAttr.SetFgColor(256);
                    break;
                case >= 40 and <= 47: // Background color
                    _curAttr.SetBgColor(param - 40);
                    break;
                case 48: // Extended background color
                    i = HandleExtendedColor(parameters, i, false);
                    break;
                case 49: // Default background
                    _curAttr.SetBgColor(257);
                    break;
                case >= 90 and <= 97: // Bright foreground color
                    _curAttr.SetFgColor(param - 90 + 8);
                    break;
                case >= 100 and <= 107: // Bright background color
                    _curAttr.SetBgColor(param - 100 + 8);
                    break;
            }
        }
    }

    private int HandleExtendedColor(Params parameters, int index, bool isForeground)
    {
        if (index + 1 >= parameters.Length)
            return index;

        var colorType = parameters.GetParam(index + 1, 0);

        if (colorType == 2 && index + 4 < parameters.Length) // RGB
        {
            var r = parameters.GetParam(index + 2, 0);
            var g = parameters.GetParam(index + 3, 0);
            var b = parameters.GetParam(index + 4, 0);
            var rgb = (r << 16) | (g << 8) | b;

            if (isForeground)
                _curAttr.SetFgColor(rgb, 1);
            else
                _curAttr.SetBgColor(rgb, 1);

            return index + 4;
        }
        else if (colorType == 5 && index + 2 < parameters.Length) // 256 color
        {
            var color = parameters.GetParam(index + 2, 0);

            if (isForeground)
                _curAttr.SetFgColor(color);
            else
                _curAttr.SetBgColor(color);

            return index + 2;
        }

        return index;
    }

    private void SetScrollRegion(Params parameters)
    {
        var top = Math.Max(parameters.GetParam(0, 1), 1) - 1;
        var bottom = Math.Max(parameters.GetParam(1, _terminal.Rows), 1) - 1;
        _buffer.SetScrollRegion(top, bottom);
        MoveCursorToHome();
    }

    private int GetAbsoluteCursorRow(int row)
    {
        if (_terminal.OriginMode)
        {
            long absoluteRow = (long)_buffer.ScrollTop + row;
            return (int)Math.Clamp(absoluteRow, _buffer.ScrollTop, _buffer.ScrollBottom);
        }

        return Math.Clamp(row, 0, _terminal.Rows - 1);
    }

    private void MoveCursorToHome()
    {
        var row = _terminal.OriginMode ? _buffer.ScrollTop : 0;
        _buffer.SetCursor(0, row);
    }

    private void WindowManipulation(Params parameters)
    {
        // CSI Ps ; Ps ; Ps t - Window manipulation (XTWINOPS)
        // Check WindowOptions permissions before firing events
        var operation = parameters.GetParam(0, 0);

        switch (operation)
        {
            case 1: // De-iconify window (restore from minimized)
                if (_terminal.Options.WindowOptions.RestoreWin)
                {
                    _terminal.RaiseWindowRestored();
                }
                break;

            case 2: // Iconify window (minimize)
                if (_terminal.Options.WindowOptions.MinimizeWin)
                {
                    _terminal.RaiseWindowMinimized();
                }
                break;

            case 3: // Move window to x, y
                if (_terminal.Options.WindowOptions.SetWinPosition)
                {
                    var x = parameters.GetParam(1, 0);
                    var y = parameters.GetParam(2, 0);
                    _terminal.RaiseWindowMoved(x, y);
                }
                break;

            case 4: // Resize window to height, width pixels
                if (_terminal.Options.WindowOptions.SetWinSizePixels)
                {
                    var height = parameters.GetParam(1, 0);
                    var width = parameters.GetParam(2, 0);
                    _terminal.RaiseWindowResized(width, height);
                }
                break;

            case 5: // Raise window to front
                if (_terminal.Options.WindowOptions.RaiseWin)
                {
                    _terminal.RaiseWindowRaised();
                }
                break;

            case 6: // Lower window to back
                if (_terminal.Options.WindowOptions.LowerWin)
                {
                    _terminal.RaiseWindowLowered();
                }
                break;

            case 7: // Refresh window
                if (_terminal.Options.WindowOptions.RefreshWin)
                {
                    _terminal.RaiseWindowRefreshed();
                }
                break;

            case 8: // Resize text area to height, width characters
                if (_terminal.Options.WindowOptions.SetWinSizeChars)
                {
                    var rows = parameters.GetParam(1, 0);
                    var cols = parameters.GetParam(2, 0);
                    if (rows > 0 && cols > 0)
                    {
                        _terminal.Resize(cols, rows);
                    }
                }
                break;

            case 9: // Maximize/restore operations
                var subOp = parameters.GetParam(1, 0);
                if (subOp == 0 && _terminal.Options.WindowOptions.RestoreWin)
                {
                    // Restore maximized window
                    _terminal.RaiseWindowRestored();
                }
                else if (subOp == 1 && _terminal.Options.WindowOptions.MaximizeWin)
                {
                    // Maximize window
                    _terminal.RaiseWindowMaximized();
                }
                break;

            case 10: // Full-screen operations
                subOp = parameters.GetParam(1, 0);
                if (subOp == 0 && _terminal.Options.WindowOptions.FullscreenWin)
                {
                    // Exit full-screen
                    _terminal.RaiseWindowFullscreened();
                }
                else if (subOp == 1 && _terminal.Options.WindowOptions.FullscreenWin)
                {
                    // Enter full-screen
                    _terminal.RaiseWindowFullscreened();
                }
                else if (subOp == 2 && _terminal.Options.WindowOptions.FullscreenWin)
                {
                    // Toggle full-screen
                    _terminal.RaiseWindowFullscreened();
                }
                break;

            case 11: // Report window state (iconified or not)
                if (_terminal.Options.WindowOptions.GetWinState)
                {
                    var args = _terminal.RaiseWindowInfoRequested(WindowInfoRequest.State);
                    if (args.Handled)
                    {
                        // Response: CSI 1 t (not iconified) or CSI 2 t (iconified)
                        var stateCode = args.IsIconified ? 2 : 1;
                        _terminal.RaiseDataReceived($"\u001b[{stateCode}t");
                    }
                }
                break;

            case 13: // Report window position
                if (_terminal.Options.WindowOptions.GetWinPosition)
                {
                    var args = _terminal.RaiseWindowInfoRequested(WindowInfoRequest.Position);
                    if (args.Handled)
                    {
                        // Response: CSI 3 ; x ; y t
                        _terminal.RaiseDataReceived($"\u001b[3;{args.X};{args.Y}t");
                    }
                }
                break;

            case 14: // Report window size in pixels
                if (_terminal.Options.WindowOptions.GetWinSizePixels)
                {
                    var args = _terminal.RaiseWindowInfoRequested(WindowInfoRequest.SizePixels);
                    if (args.Handled)
                    {
                        // Response: CSI 4 ; height ; width t
                        _terminal.RaiseDataReceived($"\u001b[4;{args.HeightPixels};{args.WidthPixels}t");
                    }
                }
                break;

            case 15: // Report screen size in pixels
                if (_terminal.Options.WindowOptions.GetScreenSizePixels)
                {
                    var args = _terminal.RaiseWindowInfoRequested(WindowInfoRequest.ScreenSizePixels);
                    if (args.Handled)
                    {
                        // Response: CSI 5 ; height ; width t
                        _terminal.RaiseDataReceived($"\u001b[5;{args.HeightPixels};{args.WidthPixels}t");
                    }
                }
                break;

            case 16: // Report character cell size in pixels
                if (_terminal.Options.WindowOptions.GetCellSizePixels)
                {
                    var args = _terminal.RaiseWindowInfoRequested(WindowInfoRequest.CellSizePixels);
                    if (args.Handled)
                    {
                        // Response: CSI 6 ; height ; width t
                        _terminal.RaiseDataReceived($"\u001b[6;{args.CellHeight};{args.CellWidth}t");
                    }
                }
                break;

            case 18: // Report text area size in characters
                if (_terminal.Options.WindowOptions.GetWinSizeChars)
                {
                    // Response: CSI 8 ; rows ; cols t
                    _terminal.RaiseDataReceived($"\u001b[8;{_terminal.Rows};{_terminal.Cols}t");
                }
                break;

            case 19: // Report screen size in characters
                if (_terminal.Options.WindowOptions.GetScreenSizePixels)
                {
                    // This is typically the same as window size for terminal apps
                    _terminal.RaiseDataReceived($"\u001b[9;{_terminal.Rows};{_terminal.Cols}t");
                }
                break;

            case 20: // Report icon label
                if (_terminal.Options.WindowOptions.GetIconTitle)
                {
                    var args = _terminal.RaiseWindowInfoRequested(WindowInfoRequest.IconTitle);
                    if (args.Handled && args.Title != null)
                    {
                        // Response: OSC L label ST
                        _terminal.RaiseDataReceived($"\u001b]L{args.Title}\u0007");
                    }
                }
                break;

            case 21: // Report window title
                if (_terminal.Options.WindowOptions.GetWinTitle)
                {
                    // Response: OSC l title ST - use the terminal's current title
                    var title = _terminal.Title ?? string.Empty;
                    _terminal.RaiseDataReceived($"\u001b]l{title}\u0007");
                }
                break;

            case 22: // Save window title
                // Push title onto stack (not typically implemented)
                break;

            case 23: // Restore window title
                // Pop title from stack (not typically implemented)
                break;
        }
    }

    private void SetCSIModeParameters(Params parameters, bool isPrivate)
    {
        for (int i = 0; i < parameters.Length; i++)
        {
            var mode = parameters.GetParam(i, 0);
            SetCSIMode(mode, isPrivate: isPrivate);
        }
    }

    private void SetCSIMode(int mode, bool isPrivate)
    {
        if (isPrivate)
        {
            // DEC Private Modes (DECSET)
            // Convert int to TerminalMode enum
            if (!Enum.IsDefined(typeof(TerminalMode), mode))
            {
                System.Diagnostics.Debug.WriteLine($"Unknown CSI private terminal mode: {mode}");
                return;
            }

            var terminalMode = (TerminalMode)mode;

            switch (terminalMode)
            {
                case TerminalMode.AppCursorKeys:
                    _terminal.ApplicationCursorKeys = true;
                    break;

                case TerminalMode.InsertMode:
                    // Mode 4: In DEC private mode context, this is SmoothScroll (DECSCLM)
                    // InsertMode and SmoothScroll share value 4 in the enum
                    // Smooth scroll is acknowledged but has no effect in modern terminals
                    break;

                case TerminalMode.ReverseVideo:
                    _terminal.ReverseVideo = true;
                    break;

                case TerminalMode.Origin:
                    _terminal.OriginMode = true;
                    MoveCursorToHome();
                    break;

                case TerminalMode.Wraparound:
                    // Mode 7: Wraparound mode
                    // Wraparound and AutoWrapMode share value 7 in the enum
                    _terminal.Options.Wraparound = true;
                    break;

                case TerminalMode.AutoRepeat:
                    // Auto repeat is typically always enabled in modern terminals
                    // This mode is acknowledged but has no effect
                    break;

                case TerminalMode.ShowCursor:
                    _terminal.CursorVisible = true;
                    break;

                case TerminalMode.NationalCharset:
                    // National replacement character set mode
                    // Acknowledged but typically no specific action needed for modern use
                    break;

                case TerminalMode.ReverseWraparound:
                    _terminal.ReverseWraparound = true;
                    break;

                case TerminalMode.AppKeypad:
                    _terminal.ApplicationKeypad = true;
                    break;

                case TerminalMode.BracketedPasteMode:
                    _terminal.BracketedPasteMode = true;
                    break;

                case TerminalMode.AltBuffer:
                    _terminal.SwitchToAltBuffer();
                    break;

                case TerminalMode.AltBufferCursor:
                    SaveCursor();
                    _terminal.SwitchToAltBuffer();
                    break;

                case TerminalMode.AltBufferFull:
                    SaveCursor();
                    _terminal.SwitchToAltBuffer();
                    _buffer.SetCursor(0, 0);
                    EraseInDisplay(new Params()); // Clear screen
                    break;

                case TerminalMode.SendFocusEvents:
                    _terminal.SendFocusEvents = true;
                    _terminal.GetMouseTracker().FocusEvents = true;
                    break;

                case TerminalMode.MouseReportClick:
                    _terminal.GetMouseTracker().TrackingMode = MouseTrackingMode.X10;
                    break;

                case TerminalMode.MouseReportNormal:
                    _terminal.GetMouseTracker().TrackingMode = MouseTrackingMode.VT200;
                    break;

                case TerminalMode.MouseReportButtonEvent:
                    _terminal.GetMouseTracker().TrackingMode = MouseTrackingMode.ButtonEvent;
                    break;

                case TerminalMode.MouseReportAnyEvent:
                    _terminal.GetMouseTracker().TrackingMode = MouseTrackingMode.AnyEvent;
                    break;

                case TerminalMode.MouseReportUtf8:
                    _terminal.GetMouseTracker().Encoding = MouseEncoding.Utf8;
                    break;

                case TerminalMode.MouseReportSgr:
                    _terminal.GetMouseTracker().Encoding = MouseEncoding.SGR;
                    break;

                case TerminalMode.MouseReportUrxvt:
                    _terminal.GetMouseTracker().Encoding = MouseEncoding.URXVT;
                    break;

                case TerminalMode.EightBitInput:
                    _terminal.EightBitInput = true;
                    break;

                case TerminalMode.NumLock:
                    // NumLock modifier handling - acknowledge but no specific action needed
                    break;

                case TerminalMode.MetaSendsEscape:
                    System.Diagnostics.Debug.WriteLine($">>> Mode {mode} MetaSendsEscape ENABLED (disabling Win32InputMode)");
                    _terminal.MetaSendsEscape = true;
                    // MetaSendsEscape is incompatible with Win32InputMode for Alt key handling
                    // When explicitly requesting ESC+char for meta keys, disable Win32 input
                    _terminal.Win32InputMode = false;
                    break;

                case TerminalMode.AltSendsEscape:
                    System.Diagnostics.Debug.WriteLine($">>> Mode {mode} AltSendsEscape ENABLED (disabling Win32InputMode)");
                    _terminal.AltSendsEscape = true;
                    // AltSendsEscape is incompatible with Win32InputMode for Alt key handling
                    // When explicitly requesting ESC+char for Alt keys, disable Win32 input
                    _terminal.Win32InputMode = false;
                    break;

                case TerminalMode.SixelDisplayMode:
                    _terminal.SixelDisplayMode = true;
                    break;

                case TerminalMode.SixelPrivateColorRegisters:
                    _terminal.SixelPrivateColorRegisters = true;
                    break;

                case TerminalMode.SixelCursorRight:
                    _terminal.SixelCursorRight = true;
                    break;

                case TerminalMode.Win32InputMode:
                    System.Diagnostics.Debug.WriteLine($">>> Mode {mode} Win32InputMode ENABLED (disabling MetaSendsEscape and AltSendsEscape)");
                    _terminal.Win32InputMode = true;
                    // Win32InputMode is incompatible with MetaSendsEscape/AltSendsEscape
                    // When enabling Win32 input mode, disable ESC+char modes
                    _terminal.MetaSendsEscape = false;
                    _terminal.AltSendsEscape = false;
                    break;

                default:
                    System.Diagnostics.Debug.WriteLine($"Unhandled CSI private terminal mode: {terminalMode}");
                    break;
            }
        }
        else
        {
            // ANSI Modes (SM)
            if (!Enum.IsDefined(typeof(TerminalMode), mode))
            {
                System.Diagnostics.Debug.WriteLine($"Unknown CSI terminal mode: {mode}");
                return;
            }

            var terminalMode = (TerminalMode)mode;

            switch (terminalMode)
            {
                case TerminalMode.InsertMode:
                    _terminal.InsertMode = true;
                    break;

                case TerminalMode.AutoWrapMode:
                    _terminal.Options.Wraparound = true;
                    break;

                default:
                    System.Diagnostics.Debug.WriteLine($"Unhandled CSI terminal mode: {terminalMode}");
                    break;
            }
        }
    }

    private void ResetCSIModeParameters(Params parameters, bool isPrivate)
    {
        for (int i = 0; i < parameters.Length; i++)
        {
            var mode = parameters.GetParam(i, 0);
            ResetCSIMode(mode, isPrivate: isPrivate);
        }
    }

    private void ResetCSIMode(int mode, bool isPrivate)
    {
        if (isPrivate)
        {
            // DEC Private Modes (DECRST)
            if (!Enum.IsDefined(typeof(TerminalMode), mode))
            {
                System.Diagnostics.Debug.WriteLine($"Unknown private reset terminal mode: {mode}");
                return;
            }

            var terminalMode = (TerminalMode)mode;

            switch (terminalMode)
            {
                case TerminalMode.AppCursorKeys:
                    _terminal.ApplicationCursorKeys = false;
                    break;

                case TerminalMode.InsertMode:
                    // Mode 4: In DEC private mode context, this is SmoothScroll (DECSCLM)
                    // Smooth scroll is acknowledged but has no effect in modern terminals
                    break;

                case TerminalMode.ReverseVideo:
                    _terminal.ReverseVideo = false;
                    break;

                case TerminalMode.Origin:
                    _terminal.OriginMode = false;
                    MoveCursorToHome();
                    break;

                case TerminalMode.Wraparound:
                    // Mode 7: Wraparound mode
                    _terminal.Options.Wraparound = false;
                    break;

                case TerminalMode.AutoRepeat:
                    // Auto repeat is typically always enabled in modern terminals
                    // This mode is acknowledged but has no effect
                    break;

                case TerminalMode.ShowCursor:
                    _terminal.CursorVisible = false;
                    break;

                case TerminalMode.NationalCharset:
                    // National replacement character set mode
                    // Acknowledged but typically no specific action needed for modern use
                    break;

                case TerminalMode.ReverseWraparound:
                    _terminal.ReverseWraparound = false;
                    break;

                case TerminalMode.AppKeypad:
                    _terminal.ApplicationKeypad = false;
                    break;

                case TerminalMode.BracketedPasteMode:
                    _terminal.BracketedPasteMode = false;
                    break;

                case TerminalMode.AltBuffer:
                    _terminal.SwitchToNormalBuffer();
                    break;

                case TerminalMode.AltBufferCursor:
                    _terminal.SwitchToNormalBuffer();
                    RestoreCursor();
                    break;

                case TerminalMode.AltBufferFull:
                    _terminal.SwitchToNormalBuffer();
                    RestoreCursor();
                    break;

                case TerminalMode.SendFocusEvents:
                    _terminal.SendFocusEvents = false;
                    _terminal.GetMouseTracker().FocusEvents = false;
                    break;

                case TerminalMode.MouseReportClick:
                case TerminalMode.MouseReportNormal:
                case TerminalMode.MouseReportButtonEvent:
                case TerminalMode.MouseReportAnyEvent:
                    _terminal.GetMouseTracker().TrackingMode = MouseTrackingMode.None;
                    break;

                case TerminalMode.MouseReportUtf8:
                case TerminalMode.MouseReportSgr:
                case TerminalMode.MouseReportUrxvt:
                    _terminal.GetMouseTracker().Encoding = MouseEncoding.Default;
                    break;

                case TerminalMode.EightBitInput:
                    _terminal.EightBitInput = false;
                    break;

                case TerminalMode.NumLock:
                    // NumLock modifier handling - acknowledge but no specific action needed
                    break;

                case TerminalMode.MetaSendsEscape:
                    System.Diagnostics.Debug.WriteLine($">>> Mode {mode} MetaSendsEscape DISABLED");
                    _terminal.MetaSendsEscape = false;
                    break;

                case TerminalMode.AltSendsEscape:
                    System.Diagnostics.Debug.WriteLine($">>> Mode {mode} AltSendsEscape DISABLED");
                    _terminal.AltSendsEscape = false;
                    break;

                case TerminalMode.SixelDisplayMode:
                    _terminal.SixelDisplayMode = false;
                    break;

                case TerminalMode.SixelPrivateColorRegisters:
                    _terminal.SixelPrivateColorRegisters = false;
                    break;

                case TerminalMode.SixelCursorRight:
                    _terminal.SixelCursorRight = false;
                    break;

                case TerminalMode.Win32InputMode:
                    System.Diagnostics.Debug.WriteLine($">>> Mode {mode} Win32InputMode DISABLED");
                    _terminal.Win32InputMode = false;
                    break;

                default:
                    System.Diagnostics.Debug.WriteLine($"Unhandled terminal mode: {terminalMode}");
                    break;
            }
        }
        else
        {
            // ANSI Modes (RM)
            if (!Enum.IsDefined(typeof(TerminalMode), mode))
            {
                System.Diagnostics.Debug.WriteLine($"Unknown CSI reset terminal mode: {mode}");
                return;
            }

            var terminalMode = (TerminalMode)mode;

            switch (terminalMode)
            {
                case TerminalMode.InsertMode:
                    _terminal.InsertMode = false;
                    break;

                case TerminalMode.AutoWrapMode:
                    _terminal.Options.Wraparound = false;
                    break;

                default:
                    System.Diagnostics.Debug.WriteLine($"Unhandled CSI reset terminal mode: {terminalMode}");
                    break;
            }
        }
    }

    // ESC Handler Implementations

    private void IndexDown()
    {
        if (_buffer.Y == _buffer.ScrollBottom)
        {
            _buffer.ScrollUp(1);
        }
        else
        {
            _buffer.SetCursor(_buffer.X, _buffer.Y + 1);
        }
    }

    private void NextLine()
    {
        IndexDown();
        _buffer.SetCursor(0, _buffer.Y);
    }

    private void ReverseIndex()
    {
        if (_buffer.Y == _buffer.ScrollTop)
        {
            _buffer.ScrollDown(1);
        }
        else
        {
            _buffer.SetCursor(_buffer.X, _buffer.Y - 1);
        }
    }

    private void ResetTerminal()
    {
        _terminal.Reset();
    }

    private void SelectCursorStyle(Params parameters)
    {
        // DECSCUSR - Select Cursor Style (CSI Ps SP q)
        var ps = parameters.GetParam(0, 1);

        CursorStyle style;
        bool blink;

        switch (ps)
        {
            case 0:
            case 1:
                style = CursorStyle.Block;
                blink = true;
                break;
            case 2:
                style = CursorStyle.Block;
                blink = false;
                break;
            case 3:
                style = CursorStyle.Underline;
                blink = true;
                break;
            case 4:
                style = CursorStyle.Underline;
                blink = false;
                break;
            case 5:
                style = CursorStyle.Bar;
                blink = true;
                break;
            case 6:
                style = CursorStyle.Bar;
                blink = false;
                break;
            default:
                // Unsupported value - ignore
                return;
        }

        _terminal.SetCursorStyle(style, blink);
    }

    private void SaveCursor()
    {
        _buffer.SavedCursorState.X = _buffer.X;
        _buffer.SavedCursorState.Y = _buffer.Y;
        _buffer.SavedCursorState.Attr = _curAttr;
    }

    private void RestoreCursor()
    {
        _buffer.SetCursor(_buffer.SavedCursorState.X, _buffer.SavedCursorState.Y);
        _curAttr = _buffer.SavedCursorState.Attr;
    }

    // Utility Methods

    private int GetStringCellWidth(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        bool supportsComplexEmoji = true;
        ushort width = 0;
        ushort lastWidth = 0;
        int regionalRuneCount = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            int runeWidth = UnicodeCalculator.GetWidth(rune);
            if (runeWidth >= 0)
            {
                if (rune.Value == Emoji.ZeroWidthJoiner || rune.Value == Emoji.ObjectReplacementCharacter)
                {
                    if (!supportsComplexEmoji)
                        // we return the first emoji as the result because terminal doesn't support chaining them
                        break;

                    if (lastWidth > 0)
                        // It joins the glyph before it, which has already been counted.
                        width -= lastWidth;
                    else
                        // Nothing in front of it to join, so it stands on its own. Subtracting unconditionally
                        // left a lone U+FFFC measuring 0, and a character measuring 0 does not move the
                        // cursor — so whatever came next printed over the top of it. ZWJ passes through here
                        // too and is unaffected, being genuinely zero-width in its own right.
                        width += (ushort)runeWidth;
                }
                else if (rune.Value == Codepoints.VariationSelectors.EmojiSymbol &&
                         lastWidth == 1)
                {
                    // adjust for the emoji presentation, which is width 2
                    width++;
                    lastWidth = 2;
                }
                else if (rune.Value == Codepoints.VariationSelectors.TextSymbol &&
                         lastWidth == 2)
                {
                    // adjust for the text presentation, which is width 1
                    width--;
                    lastWidth = 1;
                }
                else if (lastWidth > 0 &&
                         (rune.Value >= Emoji.SkinTones.Light && rune.Value <= Emoji.SkinTones.Dark ||
                          rune.Value == Codepoints.Keycap))
                {
                    // Emoji modifier (skin tone) or keycap extender should continue current glyph

                    // else: combining � ignore
                }
                else if (rune.Value >= Emoji.SkinTones.Light && rune.Value <= Emoji.SkinTones.Dark)
                {
                    // A skin tone with nothing in front of it to modify. Unicode gives these East Asian
                    // Width W, and every other terminal draws a lone one as a two-column swatch — so that is
                    // what it occupies. wcwidth answers 0 because it assumes the modifier is attached to
                    // something, and 0 meant the cursor never moved and the next character printed over the
                    // top of it: "🏽X" left an X and no swatch.
                    width += 2;
                    lastWidth = 2;
                }
                // Regional indicator symbols. These carry emoji presentation, so ONE is two columns wide and
                // a PAIR is the flag they make — also two. So the width is added on the first of a pair and
                // the second joins it rather than adding again.
                //
                // The parity used to be the other way round: width was added on the SECOND, so a single
                // indicator measured 0. This method is called once per printed character and the two halves
                // of a flag arrive separately, so the count was always 1, always odd, and the answer always
                // zero. Width 0 leaves the cursor standing still, and the next character then overwrote the
                // indicator — which is why a flag vanished from the buffer rather than merely rendering
                // oddly. Joining the two is Print's job, where state survives the call.
                else if (rune.Value >= 0x1F1E6 && rune.Value <= 0x1F1FF)
                {
                    regionalRuneCount++;
                    if (regionalRuneCount % 2 == 1)
                        width += 2;

                    lastWidth = 2;
                }
                else
                {
                    width += (ushort)runeWidth;
                }


                if (runeWidth > 0) lastWidth = (ushort)runeWidth;
            }
            // Control chars return as width < 0
            else
            {
                if (rune.Value == 0x9 /* tab */)
                {
                    // Avalonia uses hard coded 4 spaces for tabs (NOT column based tabstops), this may change in the future
                    width += 4;
                    lastWidth = 4;
                }
                else if (rune.Value == '\n')
                {
                    width += 1;
                    lastWidth = 1;
                }
            }
        }

        return width;
    }

    public void SetBuffer(Buffer.TerminalBuffer buffer)
    {
        _buffer = buffer;
    }
}
