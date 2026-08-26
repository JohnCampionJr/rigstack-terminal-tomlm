namespace XTerm.Graphics;

/// <summary>What a Kitty graphics command asks the terminal to do.</summary>
internal enum KittyAction
{
    /// <summary>a=t — take the pixels and keep them under an id, showing nothing.</summary>
    Transmit,

    /// <summary>a=T — take the pixels and show them at the cursor.</summary>
    TransmitAndDisplay,

    /// <summary>a=p — show a picture already transmitted, by id.</summary>
    Put,

    /// <summary>a=d — remove placements, and optionally the pixels behind them.</summary>
    Delete,

    /// <summary>a=q — validate and reply, changing nothing. This is how support is detected.</summary>
    Query,

    /// <summary>Something this terminal does not implement, such as animation.</summary>
    Unsupported
}

/// <summary>
/// The control data of one Kitty graphics command: the <c>key=value</c> pairs before the payload.
/// </summary>
/// <remarks>
/// <para>Keys are single letters and values are integers or single characters. Unknown keys are
/// ignored rather than refused, which the protocol requires -- it is how it grows without breaking
/// terminals that predate the addition.</para>
/// <para>Defaults follow the specification, so an absent key and an explicitly default one behave
/// alike.</para>
/// </remarks>
internal readonly struct KittyCommand
{
    /// <summary>Raw RGB, three bytes per pixel.</summary>
    public const int FormatRgb = 24;

    /// <summary>Raw RGBA, four bytes per pixel. The protocol default.</summary>
    public const int FormatRgba = 32;

    /// <summary>A PNG file.</summary>
    public const int FormatPng = 100;

    public KittyAction Action { get; init; }

    /// <summary>f — 24, 32 or 100.</summary>
    public int Format { get; init; }

    /// <summary>s — width in pixels, which raw formats must declare.</summary>
    public int Width { get; init; }

    /// <summary>v — height in pixels, which raw formats must declare.</summary>
    public int Height { get; init; }

    /// <summary>t — 'd' direct, 'f' file, 't' temporary file, 's' shared memory.</summary>
    public char Medium { get; init; }

    /// <summary>i — the client's id for the image.</summary>
    public uint ImageId { get; init; }

    /// <summary>I — an alternative to an id, answered with the id the terminal assigned.</summary>
    public uint ImageNumber { get; init; }

    /// <summary>p — the client's id for this particular appearance of the image.</summary>
    public int PlacementId { get; init; }

    /// <summary>m — whether another chunk of payload follows.</summary>
    public bool MoreChunks { get; init; }

    /// <summary>o — 'z' when the payload is zlib compressed.</summary>
    public char Compression { get; init; }

    /// <summary>q — 0 says everything, 1 suppresses success, 2 suppresses failure too.</summary>
    public int Quiet { get; init; }

    /// <summary>x, y — the top left of the part of the image to show.</summary>
    public int CropX { get; init; }
    public int CropY { get; init; }

    /// <summary>w, h — how much of the image to show. Zero means all of it.</summary>
    public int CropWidth { get; init; }
    public int CropHeight { get; init; }

    /// <summary>c, r — the cell box to fill. Zero means the image's natural size.</summary>
    public int Cols { get; init; }
    public int Rows { get; init; }

    /// <summary>C — 1 leaves the cursor where it was.</summary>
    public bool KeepCursor { get; init; }

    /// <summary>
    /// X, Y — pixel offsets within the first cell, so a picture can start off the cell boundary.
    /// </summary>
    /// <remarks>
    /// On a delete these letters mean nothing; the lower-case <c>x</c> and <c>y</c> carry the cell
    /// coordinates there. The two are separate keys and never collide.
    /// </remarks>
    public int OffsetX { get; init; }
    public int OffsetY { get; init; }

    /// <summary>z — draw order. Kept for the reply, not honoured.</summary>
    public int ZIndex { get; init; }

    /// <summary>d — which placements a delete affects.</summary>
    public char DeleteTarget { get; init; }

    /// <summary>U — the image is placed by Unicode placeholder cells rather than here.</summary>
    public bool UnicodePlaceholder { get; init; }

    /// <summary>
    /// Reads control data. Never fails: a key that makes no sense is left at its default, because
    /// a picture drawn slightly wrong is a better answer to a garbled command than silence.
    /// </summary>
    public static KittyCommand Parse(ReadOnlySpan<char> control)
    {
        var action = KittyAction.TransmitAndDisplay;   // a=t is the spec default, but see below
        var sawAction = false;
        int format = FormatRgba;
        int width = 0, height = 0;
        char medium = 'd';
        uint imageId = 0, imageNumber = 0;
        int placementId = 0;
        bool more = false;
        char compression = '\0';
        int quiet = 0;
        int cropX = 0, cropY = 0, cropWidth = 0, cropHeight = 0;
        int cols = 0, rows = 0;
        bool keepCursor = false;
        int offsetX = 0, offsetY = 0;
        int zIndex = 0;
        char deleteTarget = 'a';
        bool placeholder = false;

        foreach (var pair in Split(control, ','))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0)
                continue;

            var key = pair[0];
            var value = pair[(equals + 1)..];
            if (value.IsEmpty)
                continue;

            switch (key)
            {
                case 'a':
                    sawAction = true;
                    action = value[0] switch
                    {
                        't' => KittyAction.Transmit,
                        'T' => KittyAction.TransmitAndDisplay,
                        'p' => KittyAction.Put,
                        'd' => KittyAction.Delete,
                        'q' => KittyAction.Query,
                        _ => KittyAction.Unsupported   // 'f' and 'a', which are animation
                    };
                    break;

                case 'f': format = ReadInt(value, format); break;
                case 's': width = ReadInt(value, width); break;
                case 'v': height = ReadInt(value, height); break;
                case 't': medium = value[0]; break;
                case 'i': imageId = (uint)ReadInt(value, 0); break;
                case 'I': imageNumber = (uint)ReadInt(value, 0); break;
                case 'p': placementId = ReadInt(value, placementId); break;
                case 'm': more = ReadInt(value, 0) == 1; break;
                case 'o': compression = value[0]; break;
                case 'q': quiet = ReadInt(value, quiet); break;
                case 'x': cropX = ReadInt(value, cropX); break;
                case 'y': cropY = ReadInt(value, cropY); break;
                case 'w': cropWidth = ReadInt(value, cropWidth); break;
                case 'h': cropHeight = ReadInt(value, cropHeight); break;
                case 'c': cols = ReadInt(value, cols); break;
                case 'r': rows = ReadInt(value, rows); break;
                case 'C': keepCursor = ReadInt(value, 0) == 1; break;
                case 'X': offsetX = ReadInt(value, offsetX); break;
                case 'Y': offsetY = ReadInt(value, offsetY); break;
                case 'z': zIndex = ReadInt(value, zIndex); break;
                case 'd': deleteTarget = value[0]; break;
                case 'U': placeholder = ReadInt(value, 0) == 1; break;

                // Anything else is from a later revision of the protocol. Ignoring it is what lets
                // a newer client talk to an older terminal, and is what the specification asks for.
                default:
                    break;
            }
        }

        return new KittyCommand
        {
            // The spec's default is a=t. Applied only when no action was named at all, so a
            // continuation chunk carrying just "m=1" does not read as a fresh transmit.
            Action = sawAction ? action : KittyAction.Transmit,
            Format = format,
            Width = width,
            Height = height,
            Medium = medium,
            ImageId = imageId,
            ImageNumber = imageNumber,
            PlacementId = placementId,
            MoreChunks = more,
            Compression = compression,
            Quiet = quiet,
            CropX = cropX,
            CropY = cropY,
            CropWidth = cropWidth,
            CropHeight = cropHeight,
            Cols = cols,
            Rows = rows,
            KeepCursor = keepCursor,
            OffsetX = offsetX,
            OffsetY = offsetY,
            ZIndex = zIndex,
            DeleteTarget = deleteTarget,
            UnicodePlaceholder = placeholder
        };
    }

    /// <summary>
    /// Reads a decimal integer, keeping the fallback when the text is not one.
    /// </summary>
    /// <remarks>
    /// Saturates rather than overflowing. A value this large is already nonsense; what matters is
    /// that it stays nonsense instead of wrapping into something plausible.
    /// </remarks>
    private static int ReadInt(ReadOnlySpan<char> text, int fallback)
    {
        var negative = text[0] == '-';
        var start = negative || text[0] == '+' ? 1 : 0;
        if (start >= text.Length)
            return fallback;

        long value = 0;
        for (int i = start; i < text.Length; i++)
        {
            if (text[i] is < '0' or > '9')
                return fallback;

            value = value * 10 + (text[i] - '0');
            if (value > int.MaxValue)
                return negative ? int.MinValue : int.MaxValue;
        }

        return (int)(negative ? -value : value);
    }

    /// <summary>Splits on a separator without allocating.</summary>
    private static SpanSplitter Split(ReadOnlySpan<char> text, char separator) => new(text, separator);

    private ref struct SpanSplitter
    {
        private ReadOnlySpan<char> _remaining;
        private readonly char _separator;
        private bool _done;

        public SpanSplitter(ReadOnlySpan<char> text, char separator)
        {
            _remaining = text;
            _separator = separator;
            _done = false;
            Current = default;
        }

        public ReadOnlySpan<char> Current { get; private set; }

        public SpanSplitter GetEnumerator() => this;

        public bool MoveNext()
        {
            if (_done)
                return false;

            var at = _remaining.IndexOf(_separator);
            if (at < 0)
            {
                Current = _remaining;
                _done = true;
                return !Current.IsEmpty;
            }

            Current = _remaining[..at];
            _remaining = _remaining[(at + 1)..];
            return true;
        }
    }
}
