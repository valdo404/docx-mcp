using System.Runtime.InteropServices;
using System.Text;

namespace DocxMcp.Grpc;

/// <summary>
/// Static helper for initializing/shutting down the embedded Rust storage library.
/// Uses P/Invoke to call into the statically linked Rust staticlib.
/// </summary>
public static partial class NativeStorage
{
    [LibraryImport("*")]
    private static unsafe partial int docx_storage_init(byte* configJson);

    [LibraryImport("*")]
    private static partial int docx_storage_shutdown();

    private static readonly bool IsDebug =
        Environment.GetEnvironmentVariable("DEBUG") is not null;

    public static void Init(string localStorageDir)
    {
        if (IsDebug) Console.Error.WriteLine($"[native] Init: localStorageDir={localStorageDir}");
        // Escape backslashes and quotes in the path for JSON
        var escapedPath = localStorageDir.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var json = $$$"""{"local_storage_dir":"{{{escapedPath}}}"}""";
        if (IsDebug) Console.Error.WriteLine($"[native] Init: json={json}");
        var bytes = Encoding.UTF8.GetBytes(json + "\0");
        unsafe
        {
            fixed (byte* ptr = bytes)
            {
                var result = docx_storage_init(ptr);
                if (IsDebug) Console.Error.WriteLine($"[native] Init: docx_storage_init returned {result}");
                if (result != 0)
                    throw new InvalidOperationException("Failed to initialize native storage");
            }
        }
        if (IsDebug) Console.Error.WriteLine("[native] Init: done");
    }

    public static void Shutdown() => docx_storage_shutdown();
}
