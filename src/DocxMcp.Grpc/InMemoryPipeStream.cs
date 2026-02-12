using System.Runtime.InteropServices;

namespace DocxMcp.Grpc;

/// <summary>
/// Stream wrapper that delegates I/O to the statically linked Rust storage library
/// via P/Invoke. Used as the transport for in-memory gRPC when storage is embedded.
/// </summary>
public sealed partial class InMemoryPipeStream : Stream
{
    [LibraryImport("*")]
    private static unsafe partial long docx_pipe_read(byte* buf, nuint maxLen);

    [LibraryImport("*")]
    private static unsafe partial long docx_pipe_write(byte* buf, nuint len);

    [LibraryImport("*")]
    private static partial int docx_pipe_flush();

    private static readonly bool IsDebug =
        Environment.GetEnvironmentVariable("DEBUG") is not null;

    private static string HexDump(byte[] buf, int offset, int count)
    {
        var len = Math.Min(count, 64);
        var hex = BitConverter.ToString(buf, offset, len).Replace("-", " ");
        return count > 64 ? hex + "..." : hex;
    }

    private static unsafe string HexDumpPtr(byte* ptr, int count)
    {
        var len = Math.Min(count, 64);
        var bytes = new byte[len];
        for (int i = 0; i < len; i++) bytes[i] = ptr[i];
        var hex = BitConverter.ToString(bytes).Replace("-", " ");
        return count > 64 ? hex + "..." : hex;
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;

    public override unsafe int Read(byte[] buffer, int offset, int count)
    {
        if (IsDebug)
        {
            var tid = Environment.CurrentManagedThreadId;
            Console.Error.WriteLine($"[pipe-stream T{tid}] Read(byte[], offset={offset}, count={count})");
        }
        if (count == 0) return 0;
        fixed (byte* ptr = &buffer[offset])
        {
            var result = docx_pipe_read(ptr, (nuint)count);
            if (IsDebug)
            {
                var tid = Environment.CurrentManagedThreadId;
                if (result > 0)
                    Console.Error.WriteLine($"[pipe-stream T{tid}] Read => {result} bytes: {HexDumpPtr(ptr, (int)result)}");
                else
                    Console.Error.WriteLine($"[pipe-stream T{tid}] Read => {result}");
            }
            return result >= 0 ? (int)result : throw new IOException("Pipe read failed");
        }
    }

    public override unsafe void Write(byte[] buffer, int offset, int count)
    {
        if (IsDebug)
        {
            var tid = Environment.CurrentManagedThreadId;
            Console.Error.WriteLine($"[pipe-stream T{tid}] Write(byte[], offset={offset}, count={count}): {HexDump(buffer, offset, count)}");
        }
        if (count == 0) return;
        fixed (byte* ptr = &buffer[offset])
        {
            var result = docx_pipe_write(ptr, (nuint)count);
            if (IsDebug)
            {
                var tid = Environment.CurrentManagedThreadId;
                Console.Error.WriteLine($"[pipe-stream T{tid}] Write => {result}");
            }
            if (result < 0) throw new IOException("Pipe write failed");
        }
    }

    public override unsafe int Read(Span<byte> buffer)
    {
        if (IsDebug)
        {
            var tid = Environment.CurrentManagedThreadId;
            Console.Error.WriteLine($"[pipe-stream T{tid}] Read(Span, len={buffer.Length})");
        }
        if (buffer.Length == 0) return 0;
        fixed (byte* ptr = buffer)
        {
            var result = docx_pipe_read(ptr, (nuint)buffer.Length);
            if (IsDebug)
            {
                var tid = Environment.CurrentManagedThreadId;
                if (result > 0)
                    Console.Error.WriteLine($"[pipe-stream T{tid}] Read(Span) => {result} bytes: {HexDumpPtr(ptr, (int)result)}");
                else
                    Console.Error.WriteLine($"[pipe-stream T{tid}] Read(Span) => {result}");
            }
            return result >= 0 ? (int)result : throw new IOException("Pipe read failed");
        }
    }

    public override unsafe void Write(ReadOnlySpan<byte> buffer)
    {
        if (IsDebug)
        {
            var tid = Environment.CurrentManagedThreadId;
            Console.Error.WriteLine($"[pipe-stream T{tid}] Write(Span, len={buffer.Length})");
        }
        if (buffer.Length == 0) return;
        fixed (byte* ptr = buffer)
        {
            var result = docx_pipe_write(ptr, (nuint)buffer.Length);
            if (IsDebug)
            {
                var tid = Environment.CurrentManagedThreadId;
                Console.Error.WriteLine($"[pipe-stream T{tid}] Write(Span) => {result}");
            }
            if (result < 0) throw new IOException("Pipe write failed");
        }
    }

    // Async overrides — critical for HTTP/2 full-duplex.
    // The default Stream.ReadAsync serializes with writes on the same thread.
    // Task.Run ensures reads and writes happen on separate thread pool threads.
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        if (IsDebug)
            Console.Error.WriteLine($"[pipe-stream T{Environment.CurrentManagedThreadId}] ReadAsync(byte[], offset={offset}, count={count})");
        if (count == 0) return Task.FromResult(0);
        return Task.Run(() => Read(buffer, offset, count), ct);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (IsDebug)
            Console.Error.WriteLine($"[pipe-stream T{Environment.CurrentManagedThreadId}] ReadAsync(Memory, len={buffer.Length})");
        if (buffer.Length == 0) return new ValueTask<int>(0);
        // Memory<byte> → ArraySegment → byte[] path for safe Task.Run usage
        if (MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)buffer, out var segment))
        {
            return new ValueTask<int>(Task.Run(() => Read(segment.Array!, segment.Offset, segment.Count), ct));
        }
        // Fallback: copy through a temp buffer
        var temp = new byte[buffer.Length];
        return new ValueTask<int>(Task.Run(() =>
        {
            var n = Read(temp, 0, temp.Length);
            temp.AsSpan(0, n).CopyTo(buffer.Span);
            return n;
        }, ct));
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        if (IsDebug)
            Console.Error.WriteLine($"[pipe-stream T{Environment.CurrentManagedThreadId}] WriteAsync(byte[], offset={offset}, count={count})");
        if (count == 0) return Task.CompletedTask;
        return Task.Run(() => Write(buffer, offset, count), ct);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (IsDebug)
            Console.Error.WriteLine($"[pipe-stream T{Environment.CurrentManagedThreadId}] WriteAsync(Memory, len={buffer.Length})");
        if (buffer.Length == 0) return ValueTask.CompletedTask;
        if (MemoryMarshal.TryGetArray(buffer, out var segment))
        {
            return new ValueTask(Task.Run(() => Write(segment.Array!, segment.Offset, segment.Count), ct));
        }
        // Fallback: copy
        var temp = buffer.ToArray();
        return new ValueTask(Task.Run(() => Write(temp, 0, temp.Length), ct));
    }

    public override Task FlushAsync(CancellationToken ct)
    {
        return Task.Run(Flush, ct);
    }

    public override void Flush()
    {
        if (IsDebug)
            Console.Error.WriteLine("[pipe-stream] Flush()");
        if (docx_pipe_flush() != 0)
            throw new IOException("Pipe flush failed");
    }

    // Required abstract members (not supported for pipe stream)
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
