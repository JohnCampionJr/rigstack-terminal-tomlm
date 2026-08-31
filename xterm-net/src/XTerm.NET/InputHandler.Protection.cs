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
    /// <summary>No protection in force -- erases honour nothing.</summary>
    private const int ProtectionOff = 0;

    /// <summary>ISO protection (SPA was used last): the PLAIN erases honour guard bits.</summary>
    private const int ProtectionIso = 1;

    /// <summary>DEC protection (DECSCA was used last): only the SELECTIVE erases honour bits.</summary>
    private const int ProtectionDec = 2;

    /// <summary>
    /// Which protection discipline is currently in force. xterm keeps this as a single global
    /// gate next to the per-cell bits: SPA raises ISO, DECSCA raises DEC (whatever its parameter),
    /// and DECSTR or RIS drops it to off -- at which point every erase ignores the bits still
    /// sitting in cells. esctest leans on that: its per-test reset is DECSTR then ED 2, and the
    /// ED must sweep away the guarded characters earlier tests left behind.
    /// </summary>
    private int _protectionMode;

    internal void ResetProtectionMode() => _protectionMode = ProtectionOff;

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
        // Any DECSCA -- protecting or not -- selects the DEC discipline, exactly as xterm's
        // CASE_DECSCA sets protected_mode unconditionally.
        _protectionMode = ProtectionDec;
        if (on)
            _protectionUsed = true;
    }

    /// <summary>SPA (ESC V) -- what is written next is guarded against ED, EL and ECH.</summary>
    internal void StartProtectedArea()
    {
        _curAttr.SetGuarded(true);
        _protectionMode = ProtectionIso;
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

        if (!_protectionUsed || _protectionMode == ProtectionOff)
        {
            line.Fill(blank, start, end);
            return;
        }

        for (var col = start; col < end && col < line.Length; col++)
        {
            var cell = line[col];
            // Under ISO, guard bits stop every erase, the selective ones included -- xterm's
            // documented deviation, which esctest's DECSED knownBug encodes. Under DEC, only
            // the selective erases honour DECSCA bits; a plain ED ploughs straight through.
            if ((_protectionMode == ProtectionIso && cell.Attributes.IsGuarded())
                || (selective && cell.Attributes.IsProtected()))
                continue;
            line.SetCell(col, ref blank);
        }
    }
}
