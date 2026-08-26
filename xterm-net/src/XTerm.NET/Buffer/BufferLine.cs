using System.Collections;
using System.Text;

namespace XTerm.Buffer;

/// <summary>
/// Represents a single line in the terminal buffer.
/// Contains an array of cells and metadata about the line.
/// </summary>
public class BufferLine : IEnumerable<BufferCell>
{
    private BufferCell[] _cells;
    private int _length;
    private bool _isWrapped;
    private LineAttribute _lineAttribute;

    public int Length => _length;

    public bool IsWrapped
    {
        get => _isWrapped;
        set => _isWrapped = value;
    }

    /// <summary>
    /// Gets or sets the DEC line attribute (double-width/double-height).
    /// Set via ESC # sequences: ESC # 3 (top), ESC # 4 (bottom), ESC # 5 (normal), ESC # 6 (double-width).
    /// </summary>
    public LineAttribute LineAttribute
    {
        get => _lineAttribute;
        set
        {
            _lineAttribute = value;
            Cache = null;
        }
    }

    /// <summary>
    /// Returns true if this line has a double-width attribute (DECDWL or DECDHL).
    /// Double-width lines can only display cols/2 characters.
    /// </summary>
    public bool IsDoubleWidth => _lineAttribute.IsDoubleWidth();

    /// <summary>
    /// Cache object - this will be cleared on writes to the bufferline.
    /// </summary>
    public object? Cache { get; set; }

    public BufferLine(int cols, BufferCell? fillCell = null)
    {
        _length = cols;
        _cells = new BufferCell[cols];
        _isWrapped = false;
        _lineAttribute = LineAttribute.Normal;

        var fill = fillCell ?? BufferCell.Space;
        for (int i = 0; i < cols; i++)
        {
            _cells[i] = fill;
        }
        Cache = null;
    }

    /// <summary>
    /// Gets or sets a cell at a specific column.
    /// </summary>
    public BufferCell this[int index]
    {
        get
        {
            if (index < 0 || index >= _length)
                return BufferCell.Empty;
            return _cells[index];
        }
        set
        {
            if (index >= 0 && index < _length)
            {
                _cells[index] = value;
                Cache = null;
            }
        }
    }

    /// <summary>
    /// Sets a cell at a specific column.
    /// </summary>
    public void SetCell(int index, ref BufferCell cell)
    {
        if (index >= 0 && index < _length)
        {
            _cells[index] = cell;
            Cache = null;
        }
    }

    /// <summary>
    /// Gets the cell code point at a specific column.
    /// </summary>
    public int GetCodePoint(int index)
    {
        if (index >= 0 && index < _length)
            return _cells[index].CodePoint;
        return 0;
    }

    /// <summary>
    /// Resizes the line to a new column count.
    /// </summary>
    public void Resize(int cols, BufferCell fillCell)
    {
        if (cols == _length)
            return;

        if (cols > _length)
        {
            var newCells = new BufferCell[cols];
            Array.Copy(_cells, newCells, _length);
            for (int i = _length; i < cols; i++)
            {
                newCells[i] = fillCell;
            }
            _cells = newCells;
        }
        else
        {
            var newCells = new BufferCell[cols];
            Array.Copy(_cells, newCells, cols);
            _cells = newCells;
        }
        Cache = null;
        _length = cols;
    }

    /// <summary>
    /// Fills a range of cells with a specific cell.
    /// </summary>
    public void Fill(BufferCell fillCell, int startCol = 0, int endCol = -1)
    {
        if (endCol == -1)
            endCol = _length;

        for (int i = startCol; i < endCol && i < _length; i++)
        {
            _cells[i] = fillCell;
        }
        Cache = null;
    }

    /// <summary>
    /// Copies cells from another line.
    /// </summary>
    public void CopyCellsFrom(BufferLine src, int srcCol, int destCol, int length, bool applyInReverse)
    {
        if (applyInReverse)
        {
            for (int i = length - 1; i >= 0; i--)
            {
                if (destCol + i < _length && srcCol + i < src._length)
                {
                    _cells[destCol + i] = src._cells[srcCol + i];
                }
            }
        }
        else
        {
            for (int i = 0; i < length; i++)
            {
                if (destCol + i < _length && srcCol + i < src._length)
                {
                    _cells[destCol + i] = src._cells[srcCol + i];
                }
            }
        }
        Cache = null;
    }

    /// <summary>
    /// Translates the line to a string.
    /// </summary>
    public string TranslateToString(bool trimRight = false, int startCol = 0, int endCol = -1)
    {
        if (endCol == -1)
            endCol = _length;

        var sb = new StringBuilder();
        for (int i = startCol; i < endCol && i < _length; i++)
        {
            var cell = _cells[i];
            sb.Append(cell.Content);
        }

        if (trimRight)
        {
            return sb.ToString().TrimEnd();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Gets the width of the cell at the given column.
    /// </summary>
    public int GetWidth(int index)
    {
        if (index < 0 || index >= _length)
            return 1;
        return _cells[index].Width;
    }

    /// <summary>
    /// Returns whether the cell at the given column has content.
    /// </summary>
    public bool HasContent(int index)
    {
        if (index < 0 || index >= _length)
            return false;
        var cell = _cells[index];
        return !cell.IsSpace() && !cell.IsEmpty();
    }

    /// <summary>
    /// Replaces cells in the range [startCol, endCol) with the fill cell.
    /// </summary>
    public void ReplaceCells(int startCol, int endCol, BufferCell fillCell)
    {
        if (startCol > 0 && GetWidth(startCol - 1) == 2)
        {
            _cells[startCol - 1] = fillCell;
        }
        if (endCol < _length && GetWidth(endCol - 1) == 2)
        {
            _cells[endCol] = fillCell;
        }
        while (startCol < endCol && startCol < _length)
        {
            _cells[startCol++] = fillCell;
        }
        Cache = null;
    }

    /// <summary>
    /// Drops any image pieces on this line, leaving the cells blank but otherwise untouched.
    /// </summary>
    /// <returns>True if the line held any, which is also the signal that it needs repainting.</returns>
    public bool ClearImages()
    {
        bool found = false;
        for (int i = 0; i < _length; i++)
        {
            if (_cells[i].Image is null)
                continue;

            _cells[i].Image = null;
            _cells[i].ImageTile = 0;
            _cells[i].Content = " ";
            _cells[i].Width = 1;
            _cells[i].CodePoint = 0x20;
            found = true;
        }

        if (found)
            Cache = null;
        return found;
    }

    /// <summary>
    /// Whether any cell on this line shows part of an image. Cheap enough for a renderer to ask
    /// once per row rather than testing every cell it draws.
    /// </summary>
    public bool HasImages
    {
        get
        {
            for (int i = 0; i < _length; i++)
            {
                if (_cells[i].Image is not null)
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Gets the last non-whitespace cell index.
    /// </summary>
    public int GetTrimmedLength()
    {
        for (int i = _length - 1; i >= 0; i--)
        {
            if (!_cells[i].IsSpace() && !_cells[i].IsEmpty())
                return i + Math.Max(_cells[i].Width, 1);
        }
        return 0;
    }

    /// <summary>
    /// Clones the line.
    /// </summary>
    public BufferLine Clone()
    {
        var newLine = new BufferLine(_length);
        newLine._isWrapped = _isWrapped;
        newLine._lineAttribute = _lineAttribute;
        for (int i = 0; i < _length; i++)
        {
            newLine._cells[i] = _cells[i];
        }
        newLine.Cache = this.Cache;
        return newLine;
    }

    /// <summary>
    /// Copies the line into another line.
    /// </summary>
    public void CopyFrom(BufferLine line)
    {
        if (_length != line._length)
        {
            _cells = new BufferCell[line._length];
            _length = line._length;
        }

        for (int i = 0; i < _length; i++)
        {
            _cells[i] = line._cells[i];
        }
        _isWrapped = line._isWrapped;
        _lineAttribute = line._lineAttribute;
        this.Cache = line.Cache;
    }

    public IEnumerator<BufferCell> GetEnumerator()
    {
        return _cells.AsEnumerable().GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return _cells.GetEnumerator();
    }
}
