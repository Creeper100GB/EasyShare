using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EasyShare.Core.Security;

public enum AmsiScanResult
{
    Clean,
    Detected,
    Error,
    Unavailable,
}

public sealed class AmsiScanner : IDisposable
{
    private const uint AmsiResultDetected = 0x8000;
    private const long FullScanMaxBytes = 1L * 1024 * 1024 * 1024;
    private const long HeadTailBytes = 256L * 1024 * 1024;
    private const int ChunkBytes = 1024 * 1024;

    private IntPtr _context;
    private readonly object _lock = new();

    public bool IsAvailable { get; }

    public AmsiScanner(string appName = "EasyShare")
    {
        IsAvailable = Native.AmsiInitialize(appName, out _context) == 0;
        if (!IsAvailable) _context = IntPtr.Zero;
    }

    public AmsiScanResult ScanFile(string filePath)
    {
        if (!IsAvailable) return AmsiScanResult.Unavailable;
        if (!File.Exists(filePath)) return AmsiScanResult.Error;

        lock (_lock)
        {
            if (Native.AmsiOpenSession(_context, out var session) != 0)
                return AmsiScanResult.Error;

            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var total = fs.Length;
                var scanFull = total <= FullScanMaxBytes;
                var headBytes = scanFull ? total : HeadTailBytes;
                var tailBytes = scanFull ? 0 : Math.Min(HeadTailBytes, total / 2);
                var contentName = Path.GetFileName(filePath);
                var buffer = new byte[ChunkBytes];

                if (!ScanRegion(fs, 0, headBytes, buffer, contentName, session))
                    return AmsiScanResult.Detected;

                if (!scanFull)
                {
                    fs.Seek(total - tailBytes, SeekOrigin.Begin);
                    if (!ScanRegion(fs, total - tailBytes, tailBytes, buffer, contentName, session))
                        return AmsiScanResult.Detected;
                }

                return AmsiScanResult.Clean;
            }
            catch
            {
                return AmsiScanResult.Error;
            }
            finally
            {
                Native.AmsiCloseSession(_context, session);
            }
        }
    }

    private bool ScanRegion(Stream stream, long offset, long length, byte[] buffer, string contentName, IntPtr session)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        var remaining = length;
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(remaining, buffer.Length);
            int read = stream.Read(buffer, 0, toRead);
            if (read <= 0) break;

            uint result;
            int hr = Native.AmsiScanBuffer(_context, buffer, (uint)read, contentName, session, out result);
            if (hr != 0)
                throw new InvalidOperationException($"AmsiScanBuffer failed: 0x{hr:X8}");

            if (result >= AmsiResultDetected)
                return false;

            remaining -= read;
        }
        return true;
    }

    public void Dispose()
    {
        if (_context != IntPtr.Zero)
        {
            Native.AmsiUninitialize(_context);
            _context = IntPtr.Zero;
        }
    }

    [SupportedOSPlatform("windows")]
    private static class Native
    {
        private const string Dll = "amsi.dll";

        [DllImport(Dll, CharSet = CharSet.Unicode)]
        internal static extern int AmsiInitialize([MarshalAs(UnmanagedType.LPWStr)] string appName, out IntPtr amsiContext);

        [DllImport(Dll)]
        internal static extern void AmsiUninitialize(IntPtr amsiContext);

        [DllImport(Dll)]
        internal static extern int AmsiOpenSession(IntPtr amsiContext, out IntPtr amsiSession);

        [DllImport(Dll)]
        internal static extern void AmsiCloseSession(IntPtr amsiContext, IntPtr amsiSession);

        [DllImport(Dll)]
        internal static extern int AmsiScanBuffer(IntPtr amsiContext, byte[] buffer, uint length, [MarshalAs(UnmanagedType.LPWStr)] string contentName, IntPtr amsiSession, out uint result);
    }
}

public sealed class MalwareDetectedException : Exception
{
    public string FileName { get; }

    public MalwareDetectedException(string fileName)
        : base($"Malware detected in file: {fileName}")
    {
        FileName = fileName;
    }
}
