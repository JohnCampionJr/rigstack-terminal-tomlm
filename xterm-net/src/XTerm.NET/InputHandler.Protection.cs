using XTerm.Buffer;
using XTerm.Parser;

namespace XTerm;

/// <summary>
/// Character protection, both flavours. DECSCA (CSI Ps " q) marks characters against the
/// SELECTIVE erases, DECSED and DECSEL; ISO 6429's SPA/EPA (ESC V / ESC W) guards them against
/// the ordinary ED, EL and ECH. The two are independent -- xterm keeps them apart and esctest
/// tests them apart -- so a cell carries two bits, not one.
/// </summary>
public partial class InputHandler
{
    /// <summary>
    /// Whether any guard or protection has ever been set this session. The erase paths are hot
    /// -- a full-screen clear fills every line -- and this flag is what lets them keep the plain
    /// block fill until the first program actually uses protection.
    /// </summary>
    private bool _protectionUsed;

    /// <summary>Blanks the whole visible screen with the erase attributes -- DECCOLM's clear.</summary>
    internal void EraseWholeScreen()
    {
        for (var row = 0; row < _terminal.Rows; row++)
            EraseLineCells(_buffer.Lines[_buffer.YBase + row], 0, _terminal.Cols, selective: false);
    }

    /// <summary>DECSCA. 1 protects what is written next; 0 and 2 stop protecting.</summary>
    private void SelectCharacterProtection(Params parameters)
    {
        var on = parameters.GetParam(0, 0) == 1;
        _curAttr.SetProtected(on);
        if (on)
            _protectionUsed = true;
    }

    /// <summary>SPA (ESC V) -- what is written next is guarded against ED, EL and ECH.</summary>
    internal void StartProtectedArea()
    {
        _curAttr.SetGuarded(true);
        _protectionUsed = true;
    }

    /// <summary>EPA (ESC W) -- ends the guarded run.</summary>
    internal void EndProtectedArea() => _curAttr.SetGuarded(false);

    /// <summary>
    /// Erases <paramref name="line"/> from <paramref name="start"/> up to (exclusive)
    /// <paramref name="end"/>, honouring whichever protection applies: guarded cells always
    /// survive, and DECSCA-protected cells survive the SELECTIVE erases.
    /// </summary>
    private void EraseLineCells(BufferLine? line, int start, int end, bool selective)
    {
        if (line is null)
            return;

        var blank = BufferCell.Space;
        blank.Attributes = GetEraseAttributes();

        if (!_protectionUsed && !selective)
        {
            line.Fill(blank, start, end);
            return;
        }

        for (var col = start; col < end && col < line.Length; col++)
        {
            var cell = line[col];
            if (cell.Attributes.IsGuarded() || (selective && cell.Attributes.IsProtected()))
                continue;
            line.SetCell(col, ref blank);
        }
    }
}
