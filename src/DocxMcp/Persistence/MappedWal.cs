using System.IO.MemoryMappedFiles;
using System.Text;

namespace DocxMcp.Persistence;

/// <summary>
/// A memory-mapped write-ahead log. Appends go to the OS page cache (RAM);
/// the kernel flushes dirty pages to disk in the background.
///
/// File format: [8 bytes: data length (long)][UTF-8 JSONL data...]
/// </summary>
public sealed class MappedWal : IDisposable
{
    private const int HeaderSize = 8;
    private const long InitialCapacity = 1024 * 1024; // 1 MB

    private readonly string _path;
    private readonly object _lock = new();
    private MemoryMappedFile _mmf;
    private MemoryMappedViewAccessor _accessor;
    private long _dataLength;
    private long _capacity;

    /// <summary>
    /// Byte offsets of each JSONL line within the data region (relative to HeaderSize).
    /// _lineOffsets[i] = offset of line i, _lineOffsets[i+1] or _dataLength = end.
    /// </summary>
    private readonly List<long> _lineOffsets = new();

    public MappedWal(string path)
    {
        _path = path;

        if (File.Exists(path) && new FileInfo(path).Length >= HeaderSize)
        {
            var fileSize = new FileInfo(path).Length;
            _capacity = Math.Max(fileSize, InitialCapacity);
            _mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, _capacity);
            _accessor = _mmf.CreateViewAccessor();
            _dataLength = _accessor.ReadInt64(0);
            // Sanity check
            if (_dataLength < 0 || _dataLength > _capacity - HeaderSize)
                _dataLength = 0;
            BuildLineOffsets();
        }
        else
        {
            _capacity = InitialCapacity;
            EnsureFileWithCapacity(_path, _capacity);
            _mmf = MemoryMappedFile.CreateFromFile(_path, FileMode.Open, null, _capacity);
            _accessor = _mmf.CreateViewAccessor();
            _dataLength = 0;
            _accessor.Write(0, _dataLength);
        }
    }

    public int EntryCount
    {
        get
        {
            lock (_lock)
            {
                return _lineOffsets.Count;
            }
        }
    }

    /// <summary>
    /// Re-read the data length header from the memory-mapped file and rebuild
    /// line offsets if another process has appended to the WAL.
    /// No-op when data length is unchanged (common single-process case).
    /// </summary>
    public void Refresh()
    {
        lock (_lock)
        {
            var currentLength = _accessor.ReadInt64(0);
            if (currentLength != _dataLength
                && currentLength >= 0
                && currentLength <= _capacity - HeaderSize)
            {
                _dataLength = currentLength;
                BuildLineOffsets();
            }
        }
    }

    public void Append(string line)
    {
        lock (_lock)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            var needed = HeaderSize + _dataLength + bytes.Length;
            if (needed > _capacity)
                Grow(needed);

            // Record offset of this new line before writing
            _lineOffsets.Add(_dataLength);

            _accessor.WriteArray(HeaderSize + (int)_dataLength, bytes, 0, bytes.Length);
            _dataLength += bytes.Length;
            _accessor.Write(0, _dataLength);
            _accessor.Flush();
        }
    }

    /// <summary>
    /// Read entries in range [fromIndex, toIndex).
    /// </summary>
    public List<string> ReadRange(int fromIndex, int toIndex)
    {
        lock (_lock)
        {
            if (fromIndex < 0) fromIndex = 0;
            if (toIndex > _lineOffsets.Count) toIndex = _lineOffsets.Count;
            if (fromIndex >= toIndex)
                return new();

            var result = new List<string>(toIndex - fromIndex);
            for (int i = fromIndex; i < toIndex; i++)
            {
                result.Add(ReadLineAt(i));
            }
            return result;
        }
    }

    /// <summary>
    /// Read a single entry by index.
    /// </summary>
    public string ReadEntry(int index)
    {
        lock (_lock)
        {
            if (index < 0 || index >= _lineOffsets.Count)
                throw new ArgumentOutOfRangeException(nameof(index),
                    $"Index {index} out of range [0, {_lineOffsets.Count}).");
            return ReadLineAt(index);
        }
    }

    public List<string> ReadAll()
    {
        lock (_lock)
        {
            return ReadRangeUnlocked(0, _lineOffsets.Count);
        }
    }

    /// <summary>
    /// Keep first <paramref name="count"/> entries, discard the rest.
    /// </summary>
    public void TruncateAt(int count)
    {
        lock (_lock)
        {
            if (count <= 0)
            {
                _dataLength = 0;
                _lineOffsets.Clear();
                _accessor.Write(0, _dataLength);
                _accessor.Flush();
                return;
            }

            if (count >= _lineOffsets.Count)
                return; // nothing to truncate

            // New data length = start of the entry at 'count' (i.e., end of entry count-1)
            _dataLength = _lineOffsets[count];
            _lineOffsets.RemoveRange(count, _lineOffsets.Count - count);
            _accessor.Write(0, _dataLength);
            _accessor.Flush();
        }
    }

    public void Truncate()
    {
        lock (_lock)
        {
            _dataLength = 0;
            _lineOffsets.Clear();
            _accessor.Write(0, _dataLength);
            _accessor.Flush();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _accessor.Dispose();
            _mmf.Dispose();
        }
    }

    /// <summary>
    /// Build the offset index by scanning the data region for newline characters.
    /// Called once on construction.
    /// </summary>
    private void BuildLineOffsets()
    {
        _lineOffsets.Clear();
        if (_dataLength == 0)
            return;

        var bytes = new byte[_dataLength];
        _accessor.ReadArray(HeaderSize, bytes, 0, (int)_dataLength);

        for (long i = 0; i < _dataLength; i++)
        {
            if (i == 0 || bytes[i - 1] == (byte)'\n')
            {
                // Skip empty trailing lines
                if (i < _dataLength && bytes[i] != (byte)'\n')
                    _lineOffsets.Add(i);
            }
        }
    }

    /// <summary>
    /// Read a single line at the given offset index. Must be called under _lock.
    /// </summary>
    private string ReadLineAt(int index)
    {
        var start = _lineOffsets[index];
        var end = (index + 1 < _lineOffsets.Count)
            ? _lineOffsets[index + 1]
            : _dataLength;

        // Trim trailing newline
        var length = (int)(end - start);
        if (length > 0)
        {
            var bytes = new byte[length];
            _accessor.ReadArray(HeaderSize + (int)start, bytes, 0, length);
            // Strip trailing \n
            var text = Encoding.UTF8.GetString(bytes).TrimEnd('\n');
            return text;
        }
        return "";
    }

    private List<string> ReadRangeUnlocked(int fromIndex, int toIndex)
    {
        if (fromIndex < 0) fromIndex = 0;
        if (toIndex > _lineOffsets.Count) toIndex = _lineOffsets.Count;
        if (fromIndex >= toIndex)
            return new();

        var result = new List<string>(toIndex - fromIndex);
        for (int i = fromIndex; i < toIndex; i++)
        {
            result.Add(ReadLineAt(i));
        }
        return result;
    }

    private void Grow(long needed)
    {
        // Must be called under _lock
        _accessor.Dispose();
        _mmf.Dispose();

        var newCapacity = _capacity;
        while (newCapacity < needed)
            newCapacity *= 2;

        _capacity = newCapacity;
        EnsureFileWithCapacity(_path, _capacity);
        _mmf = MemoryMappedFile.CreateFromFile(_path, FileMode.Open, null, _capacity);
        _accessor = _mmf.CreateViewAccessor();
    }

    private static void EnsureFileWithCapacity(string path, long capacity)
    {
        using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite);
        if (fs.Length < capacity)
            fs.SetLength(capacity);
    }
}
