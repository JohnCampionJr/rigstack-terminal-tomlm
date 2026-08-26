namespace XTerm.Graphics;

/// <summary>
/// The images a client has transmitted and can ask to see again, kept by the id it gave them.
/// </summary>
/// <remarks>
/// <para>This exists because of one difference between Kitty and Sixel. A Sixel is drawn where it
/// arrives and is alive exactly as long as some cell shows it, which needs no bookkeeping at all --
/// the last cell to be overwritten or scrolled away takes the pixels with it. Kitty transmits a
/// picture under an id and may show it later, or never. An image with no placement is unreachable
/// from the cells, so something has to hold it, and something has to decide when to stop.</para>
/// <para>The rule here is a byte budget with the oldest going first. It is not the same question as
/// the on-screen budget in <c>Terminal</c>: this bounds what is held on the client's promise to use
/// it, that bounds what is actually being shown.</para>
/// </remarks>
internal sealed class ImageRegistry
{
    private readonly Dictionary<uint, TerminalImage> _byId = new();

    /// <summary>Insertion order, oldest first, so eviction has something to go on.</summary>
    private readonly LinkedList<uint> _order = new();

    private readonly Dictionary<uint, LinkedListNode<uint>> _nodes = new();

    private long _bytes;

    /// <summary>Total size of everything held.</summary>
    public long ByteCount => _bytes;

    /// <summary>How many images are held.</summary>
    public int Count => _byId.Count;

    /// <summary>The ids currently held, oldest first.</summary>
    public IEnumerable<uint> Ids => _order;

    /// <summary>
    /// Stores an image under an id, replacing anything already there.
    /// </summary>
    public void Store(uint id, TerminalImage image, long budget)
    {
        Remove(id);

        _byId[id] = image;
        _nodes[id] = _order.AddLast(id);
        _bytes += image.ByteCount;

        Trim(budget);
    }

    public bool TryGet(uint id, out TerminalImage image) => _byId.TryGetValue(id, out image!);

    /// <summary>Forgets an image. The pixels survive as long as some cell still shows them.</summary>
    public bool Remove(uint id)
    {
        if (!_byId.TryGetValue(id, out var existing))
            return false;

        _bytes -= existing.ByteCount;
        _byId.Remove(id);

        if (_nodes.TryGetValue(id, out var node))
        {
            _order.Remove(node);
            _nodes.Remove(id);
        }

        return true;
    }

    public void Clear()
    {
        _byId.Clear();
        _order.Clear();
        _nodes.Clear();
        _bytes = 0;
    }

    /// <summary>
    /// Drops the oldest images until the total fits the budget.
    /// </summary>
    /// <remarks>
    /// Dropping an id does not destroy the picture: cells showing it hold their own reference, so
    /// what is lost is the ability to place it again, not what is already on screen.
    /// </remarks>
    private void Trim(long budget)
    {
        if (budget <= 0)
            return;

        while (_bytes > budget && _order.First is { } oldest)
            Remove(oldest.Value);
    }

    /// <summary>
    /// The next id the terminal will hand out for a client that sent an image number rather than
    /// an id.
    /// </summary>
    /// <remarks>
    /// Counts down from the top of the range so it cannot collide with the small ids clients pick
    /// for themselves.
    /// </remarks>
    public uint NextAssignedId()
    {
        var candidate = _nextAssigned;
        while (_byId.ContainsKey(candidate) && candidate > 1)
            candidate--;

        _nextAssigned = candidate > 1 ? candidate - 1 : uint.MaxValue;
        return candidate;
    }

    private uint _nextAssigned = uint.MaxValue;
}
