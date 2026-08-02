using System;
using System.Text;

namespace RustServerMetrics;

internal sealed class MetricsSendBuffer
{
    private const int InitialCapacity = 1 << 16;
    private const int InitialLineCapacity = 1 << 10;

    private readonly int _maxCapacity;

    private char[] _chars = new char[InitialCapacity];
    private char[] _batch = new char[InitialCapacity];

    private int[] _lineLengths = new int[InitialLineCapacity];
    private int _lineHead;
    private int _lineCount;

    private int _head;
    private int _count;
    private long _totalDropped;

    public MetricsSendBuffer(int maxCapacity)
    {
        if (maxCapacity < InitialCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCapacity));
        }

        _maxCapacity = maxCapacity;
    }

    public int LineCount => _lineCount;
    public int CharCount => _count;
    public long TotalDropped => _totalDropped;

    public int LastBatchLineCount { get; private set; }

    public void AddDropped(int lines) => _totalDropped += lines;

    public void Append(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        if (!TryReserve(line.Length + 1)) return;

        for (var i = 0; i < line.Length; i++)
        {
            _chars[WriteIndex(i)] = line[i];
        }

        Commit(line.Length);
    }

    public void Append(StringBuilder line)
    {
        if (line == null || line.Length == 0) return;
        if (!TryReserve(line.Length + 1)) return;

        var writeStart = WriteIndex(0);
        var untilEnd = Math.Min(line.Length, _chars.Length - writeStart);
        line.CopyTo(0, _chars, writeStart, untilEnd);

        if (untilEnd < line.Length)
        {
            line.CopyTo(untilEnd, _chars, 0, line.Length - untilEnd);
        }

        Commit(line.Length);
    }

    public char[] TakeBatch(int maxLines, int maxChars, out int charCount)
    {
        charCount = 0;
        var lines = 0;

        while (lines < maxLines && lines < _lineCount)
        {
            var lineLength = _lineLengths[(_lineHead + lines) % _lineLengths.Length];
            if (lines > 0 && charCount + lineLength > maxChars) break;

            charCount += lineLength;
            lines++;
        }

        LastBatchLineCount = lines;

        if (charCount == 0) return _batch;

        if (_batch.Length < charCount)
        {
            _batch = new char[charCount];
        }

        var untilEnd = Math.Min(charCount, _chars.Length - _head);
        Array.Copy(_chars, _head, _batch, 0, untilEnd);

        if (untilEnd < charCount)
        {
            Array.Copy(_chars, 0, _batch, untilEnd, charCount - untilEnd);
        }

        _head = (_head + charCount) % _chars.Length;
        _count -= charCount;
        _lineHead = (_lineHead + lines) % _lineLengths.Length;
        _lineCount -= lines;

        return _batch;
    }

    public void Clear()
    {
        _head = 0;
        _count = 0;
        _lineHead = 0;
        _lineCount = 0;
    }

    private int WriteIndex(int offset) => (_head + _count + offset) % _chars.Length;

    private void Commit(int lineLength)
    {
        _chars[WriteIndex(lineLength)] = '\n';
        _count += lineLength + 1;

        if (_lineCount == _lineLengths.Length)
        {
            GrowLineLengths();
        }

        _lineLengths[(_lineHead + _lineCount) % _lineLengths.Length] = lineLength + 1;
        _lineCount++;
    }

    private bool TryReserve(int required)
    {
        if (required > _maxCapacity)
        {
            _totalDropped++;
            return false;
        }

        if (required > _chars.Length - _count)
        {
            Grow(required);
        }

        while (required > _chars.Length - _count)
        {
            DropOldestLine();
        }

        return true;
    }

    private void Grow(int required)
    {
        var capacity = _chars.Length;
        while (capacity < _maxCapacity && required > capacity - _count)
        {
            capacity = Math.Min(capacity * 2, _maxCapacity);
        }

        if (capacity == _chars.Length) return;

        var grown = new char[capacity];
        var untilEnd = Math.Min(_count, _chars.Length - _head);
        Array.Copy(_chars, _head, grown, 0, untilEnd);

        if (untilEnd < _count)
        {
            Array.Copy(_chars, 0, grown, untilEnd, _count - untilEnd);
        }

        _chars = grown;
        _head = 0;
    }

    private void GrowLineLengths()
    {
        var grown = new int[_lineLengths.Length * 2];
        var untilEnd = Math.Min(_lineCount, _lineLengths.Length - _lineHead);
        Array.Copy(_lineLengths, _lineHead, grown, 0, untilEnd);

        if (untilEnd < _lineCount)
        {
            Array.Copy(_lineLengths, 0, grown, untilEnd, _lineCount - untilEnd);
        }

        _lineLengths = grown;
        _lineHead = 0;
    }

    private void DropOldestLine()
    {
        if (_lineCount == 0)
        {
            _head = 0;
            _count = 0;
            return;
        }

        var lineLength = _lineLengths[_lineHead];
        _lineHead = (_lineHead + 1) % _lineLengths.Length;
        _lineCount--;
        _head = (_head + lineLength) % _chars.Length;
        _count -= lineLength;
        _totalDropped++;
    }
}
