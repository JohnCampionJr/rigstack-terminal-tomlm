using XTerm.Options;

namespace XTerm.Common;

/// <summary>
/// The terminal's 256-entry colour palette plus its foreground, background and cursor colours,
/// as read by OSC 4 and OSC 10/11/12 and reset by OSC 104 and OSC 110/111/112.
/// </summary>
/// <remarks>
/// Two layers, and the distinction is the whole point of the type. DEFAULTS come from
/// <see cref="ThemeOptions"/>, i.e. from the embedder. CURRENT values are what programs have set
/// over the top with OSC. A reset returns to the defaults, so it restores the EMBEDDER'S theme
/// rather than some factory dark palette -- otherwise any program calling OSC 104 would drag a
/// light terminal back to black, which is exactly the bug that makes light themes unusable.
/// </remarks>
public class ColorPalette
{
    /// <summary>
    /// Number of indexed colours. 0-15 are the ANSI colours, 16-231 a 6x6x6 cube, 232-255 a
    /// greyscale ramp.
    /// </summary>
    public const int Size = 256;

    private readonly int[] defaults = new int[Size];
    private readonly int[] current = new int[Size];

    private int defaultForeground;
    private int defaultBackground;
    private int defaultCursor;

    private int foregroundBacking;
    private int backgroundBacking;
    private int cursorBacking;

    /// <summary>
    /// Initializes a palette with the built-in xterm defaults.
    /// </summary>
    public ColorPalette()
        : this(null)
    {
    }

    /// <summary>
    /// Initializes a palette, taking defaults from <paramref name="theme"/> wherever it specifies
    /// one and falling back to the xterm defaults elsewhere.
    /// </summary>
    public ColorPalette(ThemeOptions? theme)
    {
        ApplyTheme(theme);
    }

    /// <summary>
    /// Fired whenever a colour changes, so a renderer can repaint. Not raised when a set is a no-op.
    /// </summary>
    public event EventHandler<ColorChangedEventArgs>? ColorChanged;

    /// <summary>
    /// The current foreground colour, as 0xRRGGBB.
    /// </summary>
    public int Foreground => foregroundBacking;

    /// <summary>
    /// The current background colour, as 0xRRGGBB.
    /// </summary>
    public int Background => backgroundBacking;

    /// <summary>
    /// The current cursor colour, as 0xRRGGBB.
    /// </summary>
    public int Cursor => cursorBacking;

    /// <summary>
    /// Whether the background is light enough that a program should choose dark text.
    /// </summary>
    /// <remarks>
    /// Uses the ITU-R BT.601 luma of the background against a mid threshold. Offered because every
    /// consumer that wants it would otherwise write the same formula, and would be likely to write
    /// it as a plain average of the channels -- which calls pure blue light.
    /// </remarks>
    public bool IsLightBackground => Luma(Background) > 0.5;

    /// <summary>
    /// Gets the current colour for an index, as 0xRRGGBB.
    /// </summary>
    public int this[int index] => current[Clamp(index)];

    /// <summary>
    /// Replaces the defaults from a theme and discards any colours programs had set.
    /// </summary>
    /// <remarks>
    /// The runtime path for an embedder following the OS light/dark setting: call this when the
    /// system theme flips. Everything is re-seeded, because a palette half in the old theme and
    /// half in the new one is not a theme.
    /// </remarks>
    public void ApplyTheme(ThemeOptions? theme)
    {
        SeedDefaults(theme);
        Array.Copy(defaults, current, Size);
        foregroundBacking = defaultForeground;
        backgroundBacking = defaultBackground;
        cursorBacking = defaultCursor;
        ColorChanged?.Invoke(this, new ColorChangedEventArgs(ColorTarget.All, -1, 0));
    }

    /// <summary>
    /// Sets one indexed colour, as OSC 4 does.
    /// </summary>
    public void SetColor(int index, int rgb)
    {
        index = Clamp(index);
        if (current[index] == rgb)
        {
            return;
        }

        current[index] = rgb;
        ColorChanged?.Invoke(this, new ColorChangedEventArgs(ColorTarget.Indexed, index, rgb));
    }

    /// <summary>
    /// Sets the foreground colour, as OSC 10 does.
    /// </summary>
    public void SetForeground(int rgb) => Set(ref foregroundBacking, rgb, ColorTarget.Foreground);

    /// <summary>
    /// Sets the background colour, as OSC 11 does.
    /// </summary>
    public void SetBackground(int rgb) => Set(ref backgroundBacking, rgb, ColorTarget.Background);

    /// <summary>
    /// Sets the cursor colour, as OSC 12 does.
    /// </summary>
    public void SetCursor(int rgb) => Set(ref cursorBacking, rgb, ColorTarget.Cursor);

    /// <summary>
    /// Restores one indexed colour to its default, as OSC 104 with a parameter does.
    /// </summary>
    public void ResetColor(int index)
    {
        index = Clamp(index);
        SetColor(index, defaults[index]);
    }

    /// <summary>
    /// Restores every indexed colour to its default, as bare OSC 104 does.
    /// </summary>
    public void ResetAllColors()
    {
        Array.Copy(defaults, current, Size);
        ColorChanged?.Invoke(this, new ColorChangedEventArgs(ColorTarget.Indexed, -1, 0));
    }

    /// <summary>
    /// Restores the foreground colour, as OSC 110 does.
    /// </summary>
    public void ResetForeground() => SetForeground(defaultForeground);

    /// <summary>
    /// Restores the background colour, as OSC 111 does.
    /// </summary>
    public void ResetBackground() => SetBackground(defaultBackground);

    /// <summary>
    /// Restores the cursor colour, as OSC 112 does.
    /// </summary>
    public void ResetCursor() => SetCursor(defaultCursor);

    private static double Luma(int rgb)
    {
        var r = ((rgb >> 16) & 0xFF) / 255.0;
        var g = ((rgb >> 8) & 0xFF) / 255.0;
        var b = (rgb & 0xFF) / 255.0;
        return (0.299 * r) + (0.587 * g) + (0.114 * b);
    }

    private static int Clamp(int index) => index < 0 ? 0 : index >= Size ? Size - 1 : index;

    private void Set(ref int backing, int rgb, ColorTarget target)
    {
        if (backing == rgb)
        {
            return;
        }

        backing = rgb;
        ColorChanged?.Invoke(this, new ColorChangedEventArgs(target, -1, rgb));
    }

    private void SeedDefaults(ThemeOptions? theme)
    {
        // 0-15: the xterm ANSI defaults, each overridable by the theme.
        var ansi = new[]
        {
            0x000000, 0xCD0000, 0x00CD00, 0xCDCD00, 0x0000EE, 0xCD00CD, 0x00CDCD, 0xE5E5E5,
            0x7F7F7F, 0xFF0000, 0x00FF00, 0xFFFF00, 0x5C5CFF, 0xFF00FF, 0x00FFFF, 0xFFFFFF,
        };

        var overrides = theme is null
            ? new string?[16]
            : new[]
            {
                theme.Black, theme.Red, theme.Green, theme.Yellow,
                theme.Blue, theme.Magenta, theme.Cyan, theme.White,
                theme.BrightBlack, theme.BrightRed, theme.BrightGreen, theme.BrightYellow,
                theme.BrightBlue, theme.BrightMagenta, theme.BrightCyan, theme.BrightWhite,
            };

        for (var i = 0; i < 16; i++)
        {
            defaults[i] = ColorSpec.TryParse(overrides[i], out var themed) ? themed : ansi[i];
        }

        // 16-231: the 6x6x6 cube. Levels are xterm's, which are not evenly spaced -- the step from
        // 0 to 95 is larger than the rest so that the darkest cell is properly black.
        var levels = new[] { 0, 95, 135, 175, 215, 255 };
        for (var r = 0; r < 6; r++)
        {
            for (var g = 0; g < 6; g++)
            {
                for (var b = 0; b < 6; b++)
                {
                    defaults[16 + (36 * r) + (6 * g) + b] =
                        (levels[r] << 16) | (levels[g] << 8) | levels[b];
                }
            }
        }

        // 232-255: the greyscale ramp, deliberately excluding both pure black and pure white,
        // which already exist in the cube.
        for (var i = 0; i < 24; i++)
        {
            var v = 8 + (i * 10);
            defaults[232 + i] = (v << 16) | (v << 8) | v;
        }

        // Defaults chosen to match what this terminal previously answered colour queries with, so
        // an embedder that sets no theme sees no change from this.
        defaultForeground = ColorSpec.TryParse(theme?.Foreground, out var fg) ? fg : 0xFFFFFF;
        defaultBackground = ColorSpec.TryParse(theme?.Background, out var bg) ? bg : 0x000000;
        defaultCursor = ColorSpec.TryParse(theme?.Cursor, out var cur) ? cur : 0xFFFFFF;
    }
}

/// <summary>
/// Which colour a <see cref="ColorPalette.ColorChanged"/> notification is about.
/// </summary>
public enum ColorTarget
{
    /// <summary>
    /// An indexed palette colour. Index -1 means every entry changed at once.
    /// </summary>
    Indexed,

    /// <summary>
    /// The foreground colour.
    /// </summary>
    Foreground,

    /// <summary>
    /// The background colour.
    /// </summary>
    Background,

    /// <summary>
    /// The cursor colour.
    /// </summary>
    Cursor,

    /// <summary>
    /// Everything was re-seeded, typically by a theme change.
    /// </summary>
    All,
}

/// <summary>
/// Describes a colour change.
/// </summary>
public class ColorChangedEventArgs : EventArgs
{
    public ColorChangedEventArgs(ColorTarget target, int index, int rgb)
    {
        Target = target;
        Index = index;
        Rgb = rgb;
    }

    /// <summary>
    /// Which colour changed.
    /// </summary>
    public ColorTarget Target { get; }

    /// <summary>
    /// The palette index for <see cref="ColorTarget.Indexed"/>, or -1 when the change was wholesale.
    /// </summary>
    public int Index { get; }

    /// <summary>
    /// The new colour as 0xRRGGBB. Not meaningful when <see cref="Index"/> is -1.
    /// </summary>
    public int Rgb { get; }
}
