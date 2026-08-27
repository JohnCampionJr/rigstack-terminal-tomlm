using System.Diagnostics;
using System.Text;
using XTerm.Common;
using XTerm.Events.Parser;

namespace XTerm.Parser;

/// <summary>
/// VT100/ANSI escape sequence parser implementing a state machine.
/// Based on Paul Williams' ANSI parser state machine.
/// </summary>
public class EscapeSequenceParser
{
    private ParserState _state;
    private readonly Params _params;
    private readonly StringBuilder _collect;
    private readonly StringBuilder _osc;
    private readonly StringBuilder _dcs;

    /// <summary>
    /// Payload characters held back so <see cref="DcsPut"/> fires once per chunk rather than once
    /// per character. A Sixel image is a few hundred thousand characters and an event apiece would
    /// cost more than the decoding does.
    /// </summary>
    private readonly char[] _dcsChunk = new char[512];
    private int _dcsChunkLength;

    /// <summary>True between a <see cref="DcsHook"/> and its matching <see cref="DcsUnhook"/>.</summary>
    private bool _dcsHooked;

    /// <summary>
    /// True when an ESC arrived mid-payload and we do not yet know whether it begins a string
    /// terminator or abandons the sequence. Resolved by the very next character.
    /// </summary>
    private bool _dcsPendingUnhook;

    /// <summary>Whether the payload is still being accumulated for the <see cref="Dcs"/> event.</summary>
    private bool _dcsAccumulating;

    /// <summary>
    /// The APC equivalent of <see cref="_dcsChunk"/>. A Kitty image arrives base64-encoded and can
    /// run to megabytes, so it is streamed a chunk at a time and never accumulated whole.
    /// </summary>
    private readonly char[] _apcChunk = new char[512];
    private int _apcChunkLength;

    /// <summary>True between an <see cref="ApcHook"/> and its matching <see cref="ApcUnhook"/>.</summary>
    private bool _apcHooked;

    /// <summary>
    /// The APC equivalent of <see cref="_dcsPendingUnhook"/>, and deliberately a separate field.
    /// Sharing one would let a DCS and an APC cross-fire each other's unhook.
    /// </summary>
    private bool _apcPendingUnhook;

    // Parser events - Standard C# event pattern
    /// <summary>
    /// Fired when printable characters are parsed.
    /// </summary>
    public event EventHandler<PrintEventArgs>? Print;

    /// <summary>
    /// Fired when control characters are executed.
    /// </summary>
    public event EventHandler<ExecuteEventArgs>? Execute;

    /// <summary>
    /// Fired when CSI sequences are parsed.
    /// </summary>
    public event EventHandler<CsiEventArgs>? Csi;

    /// <summary>
    /// Fired when ESC sequences are parsed.
    /// </summary>
    public event EventHandler<EscEventArgs>? Esc;

    /// <summary>
    /// Fired when OSC sequences are parsed.
    /// </summary>
    public event EventHandler<OscEventArgs>? Osc;

    /// <summary>
    /// Fired when a DCS sequence completes, carrying its whole payload.
    /// </summary>
    /// <remarks>
    /// Convenient for the short sequences -- DECRQSS and friends -- and useless for the long ones,
    /// because a Sixel image would have to be buffered into a single string first. So the payload
    /// is only accumulated while something is subscribed here AND the sequence stayed under
    /// <see cref="MaxAccumulatedDcsLength"/>. Anything larger is streamed and nothing else; use
    /// <see cref="DcsHook"/>/<see cref="DcsPut"/>/<see cref="DcsUnhook"/> for those.
    /// </remarks>
    public event EventHandler<DcsEventArgs>? Dcs;

    /// <summary>
    /// Fired when a DCS sequence's final character has been seen, before any payload.
    /// </summary>
    public event EventHandler<DcsHookEventArgs>? DcsHook;

    /// <summary>
    /// Fired for each chunk of a DCS payload.
    /// </summary>
    public event EventHandler<DcsPutEventArgs>? DcsPut;

    /// <summary>
    /// Fired when a DCS sequence ends, cleanly or otherwise.
    /// </summary>
    public event EventHandler<DcsUnhookEventArgs>? DcsUnhook;

    /// <summary>
    /// Fired when an APC sequence begins, before any payload.
    /// </summary>
    /// <remarks>
    /// APC has no parameter grammar in front of its payload the way CSI and DCS do -- everything
    /// after the introducer is payload, and what it means is decided by its first character. So
    /// this carries only the introducer, and a listener decides from the first chunk whether the
    /// sequence is one it wants.
    /// </remarks>
    public event EventHandler<ApcHookEventArgs>? ApcHook;

    /// <summary>
    /// Fired for each chunk of an APC payload.
    /// </summary>
    public event EventHandler<ApcPutEventArgs>? ApcPut;

    /// <summary>
    /// Fired when an APC sequence ends, cleanly or otherwise.
    /// </summary>
    public event EventHandler<ApcUnhookEventArgs>? ApcUnhook;

    /// <summary>
    /// How much of a DCS payload will be accumulated for the <see cref="Dcs"/> event. A Sixel
    /// image is unbounded and a screenful can run to megabytes; buffering that so a convenience
    /// event can hand it over as one string is how a terminal ends up holding a copy of every
    /// picture ever drawn.
    /// </summary>
    public const int MaxAccumulatedDcsLength = 4096;

    public EscapeSequenceParser()
    {
        _state = ParserState.Ground;
        _params = new Params();
        _collect = new StringBuilder();
        _osc = new StringBuilder();
        _dcs = new StringBuilder();
    }

    /// <summary>
    /// Parses input data byte by byte.
    /// </summary>
    public void Parse(string data)
    {
        foreach (var rune in data.EnumerateRunes())
        {
            ParseChar(rune.Value);
        }
    }

    /// <summary>
    /// Parses a single character/code point.
    /// </summary>
    private void ParseChar(int code)
    {
        // An ESC in a DCS payload is ambiguous until the next character arrives: "ESC \" ends the
        // sequence, anything else abandons it. Resolving it here, one character late, is what lets
        // a handler tell a finished image from a truncated one.
        if (_dcsPendingUnhook)
        {
            _dcsPendingUnhook = false;
            EndDcs(terminatedCleanly: code == 0x5C); // backslash
        }

        // The same one-character-late resolution for APC. Kept as its own flag rather than shared
        // with the DCS one: a sequence of each interleaved would otherwise close the wrong payload.
        if (_apcPendingUnhook)
        {
            _apcPendingUnhook = false;
            EndApc(terminatedCleanly: code == 0x5C);
        }

        var currentState = _state;

        // C0/C1 control characters
        if (code < 0x20 || (code >= 0x80 && code < 0xA0))
        {
            switch (currentState)
            {
                case ParserState.Ground:
                case ParserState.Escape:
                case ParserState.CsiEntry:
                case ParserState.CsiParam:
                case ParserState.CsiIntermediate:
                case ParserState.CsiIgnore:
                    OnExecute(code);
                    if (code == 0x1B) // ESC
                    {
                        Transition(ParserState.Escape);
                    }
                    return;

                case ParserState.OscString:
                    if (code == 0x1B || code == 0x07) // ESC or BEL
                    {
                        DispatchOsc();
                        Transition(code == 0x1B ? ParserState.Escape : ParserState.Ground);
                    }
                    else if (code >= 0x20)
                    {
                        OscPut(code);
                    }
                    return;
            }
        }

        // Normal state machine processing
        switch (_state)
        {
            case ParserState.Ground:
                if (code >= 0x20)
                {
                    OnPrint(code);
                }
                break;

            case ParserState.Escape:
                switch (code)
                {
                    case 0x5B: // [
                        Transition(ParserState.CsiEntry);
                        break;
                    case 0x5D: // ]
                        Transition(ParserState.OscString);
                        break;
                    case 0x50: // P
                        Transition(ParserState.DcsEntry);
                        break;
                    case 0x5F: // _  APC -- the Kitty graphics transport, so its payload is read
                        BeginApc();
                        break;
                    case 0x5E: // ^  PM
                    case 0x58: // X  SOS
                        Transition(ParserState.SosPmApcString);
                        break;
                    case >= 0x20 and < 0x30:
                        Collect(code);
                        Transition(ParserState.EscapeIntermediate);
                        break;
                    case >= 0x30 and < 0x7F:
                        DispatchEsc(code);
                        Transition(ParserState.Ground);
                        break;
                    default:
                        Transition(ParserState.Ground);
                        break;
                }
                break;

            case ParserState.EscapeIntermediate:
                if (code >= 0x20 && code < 0x30)
                {
                    Collect(code);
                }
                else if (code >= 0x30 && code < 0x7F)
                {
                    DispatchEsc(code);
                    Transition(ParserState.Ground);
                }
                break;

            case ParserState.CsiEntry:
                if (code >= 0x3C && code <= 0x3F) // Private parameter markers (<, =, >, ?)
                {
                    Collect(code);
                }
                else if (code >= 0x30 && code < 0x3C) // 0-9, :, ;
                {
                    Param(code);
                    Transition(ParserState.CsiParam);
                }
                else if (code >= 0x40 && code < 0x7F)
                {
                    DispatchCsi(code);
                    Transition(ParserState.Ground);
                }
                else if (code >= 0x20 && code < 0x30)
                {
                    Collect(code);
                    Transition(ParserState.CsiIntermediate);
                }
                break;

            case ParserState.CsiParam:
                if (code >= 0x30 && code < 0x40)
                {
                    Param(code);
                }
                else if (code >= 0x40 && code < 0x7F)
                {
                    DispatchCsi(code);
                    Transition(ParserState.Ground);
                }
                else if (code >= 0x20 && code < 0x30)
                {
                    Collect(code);
                    Transition(ParserState.CsiIntermediate);
                }
                else if (code == 0x3A) // :
                {
                    // Sub-parameter separator
                    Transition(ParserState.CsiIgnore);
                }
                break;

            case ParserState.CsiIntermediate:
                if (code >= 0x20 && code < 0x30)
                {
                    Collect(code);
                }
                else if (code >= 0x40 && code < 0x7F)
                {
                    DispatchCsi(code);
                    Transition(ParserState.Ground);
                }
                break;

            case ParserState.CsiIgnore:
                if (code >= 0x40 && code < 0x7F)
                {
                    Transition(ParserState.Ground);
                }
                break;

            case ParserState.OscString:
                OscPut(code);
                break;

            case ParserState.ApcString:
                // Mirrors DcsPassthrough exactly; see the reasoning there. APC is where Kitty
                // graphics arrive, so unlike SOS and PM below, the payload is kept.
                if (code == 0x9C) // ST
                {
                    EndApc(terminatedCleanly: true);
                    Transition(ParserState.Ground);
                }
                else if (code == 0x1B) // ESC, possibly the first half of ESC \
                {
                    _apcPendingUnhook = true;
                    Transition(ParserState.Escape);
                }
                else if (code == 0x18 || code == 0x1A) // CAN, SUB — an explicit abort
                {
                    EndApc(terminatedCleanly: false);
                    Transition(ParserState.Ground);
                }
                else if (code == 0x7F) { /* DEL is not payload */ }
                else
                {
                    ApcPutChar(code);
                }
                break;

            case ParserState.SosPmApcString:
                // SOS and PM are consumed whole and answered by nobody. What matters is LEAVING them —
                // and this state had no case here at all. ESC _ , ESC ^ and ESC X were entered and never
                // exited, so the parser sat in that state discarding every byte that followed it. One
                // kitty graphics query and the terminal stopped answering anything, permanently.
                //
                // ESC moves to Escape rather than Ground so the backslash of a two-byte ST is consumed as
                // part of the terminator, which is what OSC already does. Dropping straight to Ground left
                // that backslash to be printed as text.
                if (code == 0x9C) // ST
                {
                    Transition(ParserState.Ground);
                }
                else if (code == 0x1B) // ESC, the first half of ESC \
                {
                    Transition(ParserState.Escape);
                }
                break;

            // ---- DCS ------------------------------------------------------------------------
            // The prologue states mirror their CSI counterparts exactly, because the grammar in
            // front of the final character is the same one. What differs is the final character:
            // CSI dispatches and returns to Ground, DCS opens a payload that runs until ST.

            case ParserState.DcsEntry:
                if (code == 0x9C) { Transition(ParserState.Ground); }
                else if (code == 0x1B) { Transition(ParserState.Escape); }
                else if (code == 0x18 || code == 0x1A) { Transition(ParserState.Ground); }
                else if (code < 0x20 || code == 0x7F) { /* ignored */ }
                else if (code >= 0x3C && code <= 0x3F) // private markers <, =, >, ?
                {
                    Collect(code);
                    Transition(ParserState.DcsParam);
                }
                else if (code >= 0x30 && code < 0x3C) // 0-9, :, ;
                {
                    if (code == 0x3A) { Transition(ParserState.DcsIgnore); }
                    else { Param(code); Transition(ParserState.DcsParam); }
                }
                else if (code >= 0x20 && code < 0x30) // intermediates
                {
                    Collect(code);
                    Transition(ParserState.DcsIntermediate);
                }
                else if (code >= 0x40 && code < 0x7F)
                {
                    BeginDcs(code);
                }
                break;

            case ParserState.DcsParam:
                if (code == 0x9C) { Transition(ParserState.Ground); }
                else if (code == 0x1B) { Transition(ParserState.Escape); }
                else if (code == 0x18 || code == 0x1A) { Transition(ParserState.Ground); }
                else if (code < 0x20 || code == 0x7F) { /* ignored */ }
                else if (code >= 0x30 && code < 0x3C) // 0-9, ;
                {
                    if (code == 0x3A) { Transition(ParserState.DcsIgnore); }
                    else { Param(code); }
                }
                else if (code >= 0x3C && code <= 0x3F)
                {
                    // A private marker is only legal before the parameters. Arriving here it is
                    // malformed, and the sequence is discarded rather than half-honoured.
                    Transition(ParserState.DcsIgnore);
                }
                else if (code >= 0x20 && code < 0x30)
                {
                    Collect(code);
                    Transition(ParserState.DcsIntermediate);
                }
                else if (code >= 0x40 && code < 0x7F)
                {
                    BeginDcs(code);
                }
                break;

            case ParserState.DcsIntermediate:
                if (code == 0x9C) { Transition(ParserState.Ground); }
                else if (code == 0x1B) { Transition(ParserState.Escape); }
                else if (code == 0x18 || code == 0x1A) { Transition(ParserState.Ground); }
                else if (code < 0x20 || code == 0x7F) { /* ignored */ }
                else if (code >= 0x20 && code < 0x30)
                {
                    Collect(code);
                }
                else if (code >= 0x30 && code < 0x40)
                {
                    // Parameters after an intermediate are out of order; discard the sequence.
                    Transition(ParserState.DcsIgnore);
                }
                else if (code >= 0x40 && code < 0x7F)
                {
                    BeginDcs(code);
                }
                break;

            case ParserState.DcsIgnore:
                if (code == 0x9C) { Transition(ParserState.Ground); }
                else if (code == 0x1B) { Transition(ParserState.Escape); }
                else if (code == 0x18 || code == 0x1A) { Transition(ParserState.Ground); }
                break;

            case ParserState.DcsPassthrough:
                if (code == 0x9C) // ST
                {
                    EndDcs(terminatedCleanly: true);
                    Transition(ParserState.Ground);
                }
                else if (code == 0x1B) // ESC, possibly the first half of ESC \
                {
                    // Do not decide yet. The next character says whether this terminated the
                    // sequence or abandoned it; ParseChar resolves it on the way in.
                    _dcsPendingUnhook = true;
                    Transition(ParserState.Escape);
                }
                else if (code == 0x18 || code == 0x1A) // CAN, SUB — an explicit abort
                {
                    EndDcs(terminatedCleanly: false);
                    Transition(ParserState.Ground);
                }
                else if (code == 0x7F) { /* DEL is not payload */ }
                else
                {
                    DcsPutChar(code);
                }
                break;
        }
    }

    private void Transition(ParserState newState)
    {
        // Exit actions
        switch (_state)
        {
            case ParserState.CsiEntry:
            case ParserState.CsiParam:
            case ParserState.CsiIntermediate:
            case ParserState.CsiIgnore:
                if (newState != ParserState.CsiParam && newState != ParserState.CsiIntermediate && newState != ParserState.CsiIgnore)
                {
                    _params.Reset();
                    _collect.Clear();
                }
                break;
        }

        _state = newState;

        // Entry actions
        switch (newState)
        {
            case ParserState.CsiEntry:
            case ParserState.DcsEntry:
                _params.Reset();
                _collect.Clear();
                _params.AddParam(0);
                break;

            case ParserState.OscString:
                _osc.Clear();
                break;
        }
    }

    /// <summary>
    /// Raises the Print event.
    /// </summary>
    protected virtual void OnPrint(int code)
    {
        Print?.Invoke(this, new PrintEventArgs(char.ConvertFromUtf32(code)));
    }

    /// <summary>
    /// Raises the Execute event.
    /// </summary>
    protected virtual void OnExecute(int code)
    {
        Execute?.Invoke(this, new ExecuteEventArgs(code));
    }

    private void Collect(int code)
    {
        _collect.Append((char)code);
    }

    private void Param(int code)
    {
        if (code == 0x3B) // ;
        {
            _params.AddParam(0);
        }
        else if (code >= 0x30 && code <= 0x39) // 0-9
        {
            var digit = code - 0x30;
            
            // Get current value of last parameter and update it
            var currentValue = _params.GetParam(_params.Length - 1, 0);
            var newValue = currentValue * 10 + digit;
            _params.UpdateLastParam(newValue);
        }
    }

    private void DispatchCsi(int code)
    {
        var finalChar = ((char)code).ToString();
        // Clone params so handlers get their own copy
        var paramsClone = _params.Clone();
        // Collected characters come BEFORE the final character (e.g., "?" before "h" gives "?h")
        var identifier = _collect.ToString() + finalChar;
        OnCsi(identifier, paramsClone);
    }

    /// <summary>
    /// Raises the Csi event.
    /// </summary>
    protected virtual void OnCsi(string identifier, Params parameters)
    {
        Csi?.Invoke(this, new CsiEventArgs(identifier, parameters));
    }

    private void DispatchEsc(int code)
    {
        var finalChar = ((char)code).ToString();
        OnEsc(finalChar, _collect.ToString());
    }

    /// <summary>
    /// Raises the Esc event.
    /// </summary>
    protected virtual void OnEsc(string finalChar, string collected)
    {
        Esc?.Invoke(this, new EscEventArgs(finalChar, collected));
    }

    private void OscPut(int code)
    {
        _osc.Append(char.ConvertFromUtf32(code));
    }

    /// <summary>
    /// Handles the final character of a DCS: announces the sequence and opens its payload.
    /// </summary>
    private void BeginDcs(int code)
    {
        // Read the prologue before transitioning — Transition's entry action for a later state is
        // free to clear it.
        var identifier = _collect.ToString() + (char)code;
        _collect.Clear();
        var paramsClone = _params.Clone();
        _dcsChunkLength = 0;
        _dcs.Clear();
        _dcsHooked = true;

        // Only pay for accumulation if somebody is actually listening for the whole-payload event.
        _dcsAccumulating = Dcs != null;

        Transition(ParserState.DcsPassthrough);
        OnDcsHook(identifier, paramsClone);
    }

    /// <summary>
    /// Adds one character to the payload, flushing to <see cref="DcsPut"/> a chunk at a time.
    /// </summary>
    private void DcsPutChar(int code)
    {
        if (code > 0xFFFF)
        {
            // Not something Sixel or DECRQSS produce, but the parser is rune-based and dropping
            // half a surrogate pair into the payload would be worse than spending two slots.
            var surrogates = char.ConvertFromUtf32(code);
            foreach (var c in surrogates)
                DcsPutChar(c);
            return;
        }

        if (_dcsAccumulating)
        {
            if (_dcs.Length < MaxAccumulatedDcsLength)
                _dcs.Append((char)code);
            else
                _dcsAccumulating = false; // too big to hand over as one string; stop paying for it
        }

        _dcsChunk[_dcsChunkLength++] = (char)code;
        if (_dcsChunkLength == _dcsChunk.Length)
            FlushDcsChunk();
    }

    private void FlushDcsChunk()
    {
        if (_dcsChunkLength == 0)
            return;

        var length = _dcsChunkLength;
        _dcsChunkLength = 0;
        OnDcsPut(new ReadOnlyMemory<char>(_dcsChunk, 0, length));
    }

    /// <summary>
    /// Closes an open DCS payload. Safe to call when none is open, which is what makes it usable
    /// from <see cref="Reset"/> and from every abort path without a guard at each call site.
    /// </summary>
    private void EndDcs(bool terminatedCleanly)
    {
        if (!_dcsHooked)
            return;

        _dcsHooked = false;
        FlushDcsChunk();

        if (_dcsAccumulating)
        {
            _dcsAccumulating = false;
            OnDcs(_dcs.ToString(), _params.Clone());
        }
        _dcs.Clear();

        OnDcsUnhook(terminatedCleanly);
    }

    /// <summary>
    /// Opens an APC payload. Everything after the introducer belongs to it.
    /// </summary>
    private void BeginApc()
    {
        _apcChunkLength = 0;
        _apcHooked = true;

        Transition(ParserState.ApcString);
        OnApcHook('_');
    }

    /// <summary>
    /// Adds one character to the payload, flushing to <see cref="ApcPut"/> a chunk at a time.
    /// </summary>
    private void ApcPutChar(int code)
    {
        if (code > 0xFFFF)
        {
            // Kitty payloads are base64 and never leave ASCII, but the parser is rune-based and
            // half a surrogate pair in the stream would be worse than spending two slots.
            foreach (var c in char.ConvertFromUtf32(code))
                ApcPutChar(c);
            return;
        }

        _apcChunk[_apcChunkLength++] = (char)code;
        if (_apcChunkLength == _apcChunk.Length)
            FlushApcChunk();
    }

    private void FlushApcChunk()
    {
        if (_apcChunkLength == 0)
            return;

        var length = _apcChunkLength;
        _apcChunkLength = 0;
        OnApcPut(new ReadOnlyMemory<char>(_apcChunk, 0, length));
    }

    /// <summary>
    /// Closes an open APC payload. Safe to call when none is open, which is what lets it be used
    /// from <see cref="Reset"/> and from every abort path without a guard at each call site.
    /// </summary>
    private void EndApc(bool terminatedCleanly)
    {
        if (!_apcHooked)
            return;

        _apcHooked = false;
        FlushApcChunk();
        OnApcUnhook(terminatedCleanly);
    }

    /// <summary>
    /// Raises the ApcHook event.
    /// </summary>
    protected virtual void OnApcHook(char introducer)
    {
        ApcHook?.Invoke(this, new ApcHookEventArgs(introducer));
    }

    /// <summary>
    /// Raises the ApcPut event.
    /// </summary>
    protected virtual void OnApcPut(ReadOnlyMemory<char> data)
    {
        ApcPut?.Invoke(this, new ApcPutEventArgs(data));
    }

    /// <summary>
    /// Raises the ApcUnhook event.
    /// </summary>
    protected virtual void OnApcUnhook(bool terminatedCleanly)
    {
        ApcUnhook?.Invoke(this, new ApcUnhookEventArgs(terminatedCleanly));
    }

    /// <summary>
    /// Raises the DcsHook event.
    /// </summary>
    protected virtual void OnDcsHook(string identifier, Params parameters)
    {
        DcsHook?.Invoke(this, new DcsHookEventArgs(identifier, parameters));
    }

    /// <summary>
    /// Raises the DcsPut event.
    /// </summary>
    protected virtual void OnDcsPut(ReadOnlyMemory<char> data)
    {
        DcsPut?.Invoke(this, new DcsPutEventArgs(data));
    }

    /// <summary>
    /// Raises the DcsUnhook event.
    /// </summary>
    protected virtual void OnDcsUnhook(bool terminatedCleanly)
    {
        DcsUnhook?.Invoke(this, new DcsUnhookEventArgs(terminatedCleanly));
    }

    /// <summary>
    /// Raises the Dcs event.
    /// </summary>
    protected virtual void OnDcs(string data, Params parameters)
    {
        Dcs?.Invoke(this, new DcsEventArgs(data, parameters));
    }

    private void DispatchOsc()
    {
        OnOsc(_osc.ToString());
    }

    /// <summary>
    /// Raises the Osc event.
    /// </summary>
    protected virtual void OnOsc(string data)
    {
        Osc?.Invoke(this, new OscEventArgs(data));
    }

    /// <summary>
    /// Resets the parser to initial state.
    /// </summary>
    public void Reset()
    {
        // A reset mid-image abandons it. Say so, rather than leaving a decoder open forever
        // waiting for a payload that will never arrive.
        EndDcs(terminatedCleanly: false);
        EndApc(terminatedCleanly: false);

        _state = ParserState.Ground;
        _params.Reset();
        _collect.Clear();
        _osc.Clear();
        _dcs.Clear();
        _dcsChunkLength = 0;
        _dcsPendingUnhook = false;
        _dcsAccumulating = false;
        _apcChunkLength = 0;
        _apcPendingUnhook = false;
    }
}
