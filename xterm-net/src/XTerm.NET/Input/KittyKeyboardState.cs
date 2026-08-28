namespace XTerm.Input;

/// <summary>
/// The terminal-side state of the Kitty keyboard protocol: which enhancement flags are active,
/// and the stack an application pushes them to on entry and pops on exit.
/// </summary>
/// <remarks>
/// <para>Kept PER SCREEN. A full-screen application sets its flags on the alternate screen, and
/// they must not leak: if vim crashes without popping, switching back to the main screen still
/// restores the shell's flags, because the shell's screen kept its own. This is the same rule the
/// protocol's designers wrote for exactly that failure.</para>
/// <para>Which screen's stack a push or pop lands on follows the active buffer, tracked here by
/// <see cref="SwitchScreen"/> so the CSI handlers do not each re-derive it.</para>
/// </remarks>
public sealed class KittyKeyboardState
{
    /// <summary>
    /// The stack depth an application can accumulate before the oldest entry is dropped.
    /// A push beyond it evicts from the bottom rather than failing, per the spec's advice that
    /// terminals bound the stack — an application looping on push must not grow memory forever.
    /// </summary>
    private const int MaxStackDepth = 16;

    private readonly List<KittyKeyboardFlags> _mainStack = new();
    private readonly List<KittyKeyboardFlags> _altStack = new();
    private KittyKeyboardFlags _savedFlags;
    private bool _onAltScreen;

    /// <summary>The enhancement flags active on the current screen.</summary>
    public KittyKeyboardFlags Flags { get; private set; }

    /// <summary>
    /// Sets the flags per <c>CSI = flags ; mode u</c>: mode 1 assigns, mode 2 sets only the
    /// given bits, mode 3 clears only the given bits.
    /// </summary>
    internal void Set(KittyKeyboardFlags flags, int mode)
    {
        switch (mode)
        {
            case 1:
                Flags = flags;
                break;
            case 2:
                Flags |= flags;
                break;
            case 3:
                Flags &= ~flags;
                break;
        }
    }

    /// <summary>Pushes the current flags onto this screen's stack and activates new ones.</summary>
    internal void Push(KittyKeyboardFlags flags)
    {
        var stack = _onAltScreen ? _altStack : _mainStack;
        if (stack.Count >= MaxStackDepth)
            stack.RemoveAt(0);
        stack.Add(Flags);
        Flags = flags;
    }

    /// <summary>
    /// Pops entries from this screen's stack per <c>CSI &lt; count u</c>. Popping past the bottom
    /// leaves the flags at zero: an application that over-pops asked for a clean slate.
    /// </summary>
    internal void Pop(int count)
    {
        var stack = _onAltScreen ? _altStack : _mainStack;
        for (var i = 0; i < count && stack.Count > 0; i++)
        {
            Flags = stack[^1];
            stack.RemoveAt(stack.Count - 1);
        }
        if (stack.Count == 0 && count > 0)
            Flags = KittyKeyboardFlags.None;
    }

    /// <summary>
    /// Moves to the other screen, parking the current screen's flags and restoring the
    /// destination's. Called from the buffer switch itself so every path that changes screens
    /// carries the flags with it.
    /// </summary>
    internal void SwitchScreen(bool toAltScreen)
    {
        if (_onAltScreen == toAltScreen)
            return;
        (Flags, _savedFlags) = (_savedFlags, Flags);
        _onAltScreen = toAltScreen;
    }

    /// <summary>
    /// Returns everything to protocol-off. RIS is how a user recovers from an application that
    /// set flags and died, so reset must clear both screens and both stacks.
    /// </summary>
    internal void Reset()
    {
        Flags = KittyKeyboardFlags.None;
        _savedFlags = KittyKeyboardFlags.None;
        _mainStack.Clear();
        _altStack.Clear();
        _onAltScreen = false;
    }
}
