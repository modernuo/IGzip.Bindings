using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.IO.Compression;

public enum IGzipResult
{
    Success = 0,
    BadData = 1,
    ShortInput = 2,
    InsufficientSpace = 3,
}

/// <summary>
/// ISA-L igzip levels. Level 0 is the fastest but uses static Huffman tables and compresses much worse;
/// level 1 is the usual choice for small payloads; level 3 trades speed for a little ratio.
/// </summary>
public enum IGzipCompressionLevel
{
    Level0 = 0,
    Level1 = 1,
    Level2 = 2,
    Level3 = 3,
}

/// <summary>Stream framing. Values match ISA-L's IGZIP_*/ISAL_* wrapper flags.</summary>
public enum IGzipFormat
{
    /// <summary>Raw DEFLATE (RFC 1951): no header or trailer.</summary>
    Deflate = 0,

    /// <summary>gzip (RFC 1952): 10-byte header, CRC-32 and size trailer.</summary>
    Gzip = 1,

    /// <summary>zlib (RFC 1950): 2-byte header, Adler-32 trailer. What zlib's compress()/uncompress() use.</summary>
    Zlib = 3,
}

/// <summary>
/// One-shot compression and decompression through ISA-L's stateless igzip API. Each instance owns its native
/// stream state (about 170 KiB plus the level buffer) and is not thread safe; keep one per thread and reuse it.
/// </summary>
public sealed unsafe class IGzipBinding : IDisposable
{
    // struct isal_zstream / struct inflate_state. These layouts have been identical in every release from 2.28
    // through 2.32.1 on Windows, glibc and musl; CI re-checks them against each distro's headers.
    internal const int ZStreamSize = 82384;
    internal const int InflateStateSize = 87368;

    private const int ZNextIn = 0;
    private const int ZAvailIn = 8;
    private const int ZNextOut = 16;
    private const int ZAvailOut = 24;
    private const int ZLevel = 40;
    private const int ZLevelBufSize = 44;
    private const int ZLevelBuf = 48;
    private const int ZEndOfStream = 56;
    private const int ZFlush = 58;
    private const int ZGzipFlag = 60;

    private const int INextOut = 0;
    private const int IAvailOut = 8;
    private const int INextIn = 16;
    private const int IAvailIn = 32;
    private const int ICrcFlag = 21172;

    private const int CompOk = 0;
    private const int DecompOk = 0;
    private const int DecompEndInput = 1;
    private const int DecompOutOverflow = 2;

    private const int Alignment = 64;

    private readonly byte* _deflate;
    private readonly byte* _inflate;
    private readonly byte* _levelBuf;
    private readonly uint _levelBufSize;
    private readonly int _level;
    private readonly IGzipFormat _format;
    private bool _disposed;

    public IGzipBinding(IGzipCompressionLevel compressionLevel = IGzipCompressionLevel.Level1, IGzipFormat format = IGzipFormat.Zlib)
    {
        if ((uint)compressionLevel > (uint)IGzipCompressionLevel.Level3)
        {
            throw new ArgumentOutOfRangeException(nameof(compressionLevel));
        }

        if (format is not (IGzipFormat.Deflate or IGzipFormat.Gzip or IGzipFormat.Zlib))
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        // Touch the native library before allocating, so a missing library surfaces as DllNotFoundException
        // without leaking the blocks below.
        NativeMethods.EnsureLoaded();

        _level = (int)compressionLevel;
        _format = format;
        _levelBufSize = LevelBufferSize(compressionLevel);

        _deflate = AllocZeroed((nuint)ZStreamSize);
        _inflate = AllocZeroed((nuint)InflateStateSize);
        _levelBuf = _levelBufSize == 0 ? null : AllocZeroed(_levelBufSize);
    }

    public IGzipCompressionLevel CompressionLevel => (IGzipCompressionLevel)_level;

    public IGzipFormat Format => _format;

    // The "small" level buffers from igzip_lib.h (ISAL_DEF_LVLx_SMALL). Larger buffers made no difference to
    // output or speed on inputs up to 64 KiB.
    private static uint LevelBufferSize(IGzipCompressionLevel level) => level switch
    {
        IGzipCompressionLevel.Level1 => 86016,
        IGzipCompressionLevel.Level2 => 135168,
        IGzipCompressionLevel.Level3 => 151552,
        _ => 0,
    };

    /// <summary>
    /// Upper bound on <see cref="Pack"/> output for <paramref name="inputLength"/> bytes. Incompressible input
    /// falls back to stored blocks, so this is the input plus stored-block headers, the largest wrapper and slack.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int MaxPackSize(int inputLength) => inputLength + 5 * (inputLength / 0xFFFF + 1) + 64;

    /// <summary>Compresses <paramref name="source"/> into <paramref name="dest"/>.</summary>
    /// <returns>Bytes written, or 0 if <paramref name="dest"/> is too small.</returns>
    public int Pack(Span<byte> dest, ReadOnlySpan<byte> source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        fixed (byte* inputPtr = source)
        fixed (byte* outputPtr = dest)
        {
            var z = _deflate;
            NativeMethods.isal_deflate_stateless_init(z);

            *(uint*)(z + ZLevel) = (uint)_level;
            *(uint*)(z + ZLevelBufSize) = _levelBufSize;
            *(byte**)(z + ZLevelBuf) = _levelBuf;
            *(ushort*)(z + ZEndOfStream) = 1;
            *(ushort*)(z + ZFlush) = 0; // NO_FLUSH
            *(ushort*)(z + ZGzipFlag) = (ushort)_format;
            *(byte**)(z + ZNextIn) = inputPtr;
            *(uint*)(z + ZAvailIn) = (uint)source.Length;
            *(byte**)(z + ZNextOut) = outputPtr;
            *(uint*)(z + ZAvailOut) = (uint)dest.Length;

            if (NativeMethods.isal_deflate_stateless(z) != CompOk || *(uint*)(z + ZAvailIn) != 0)
            {
                return 0;
            }

            return dest.Length - (int)*(uint*)(z + ZAvailOut);
        }
    }

    /// <summary>
    /// Decompresses a complete stream. For <see cref="IGzipFormat.Zlib"/> and <see cref="IGzipFormat.Gzip"/> the
    /// header is validated and the trailer checksum verified.
    /// </summary>
    public IGzipResult Unpack(Span<byte> dest, ReadOnlySpan<byte> source, out int uncompressedLength)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int rc;
        uint availOut;
        fixed (byte* inputPtr = source)
        fixed (byte* outputPtr = dest)
        {
            var s = _inflate;
            NativeMethods.isal_inflate_init(s);

            *(uint*)(s + ICrcFlag) = (uint)_format;
            *(byte**)(s + INextIn) = inputPtr;
            *(uint*)(s + IAvailIn) = (uint)source.Length;
            *(byte**)(s + INextOut) = outputPtr;
            *(uint*)(s + IAvailOut) = (uint)dest.Length;

            rc = NativeMethods.isal_inflate_stateless(s);
            availOut = *(uint*)(s + IAvailOut);
        }

        if (rc == DecompOk)
        {
            uncompressedLength = dest.Length - (int)availOut;
            return IGzipResult.Success;
        }

        uncompressedLength = 0;
        return rc switch
        {
            DecompOutOverflow => IGzipResult.InsufficientSpace,
            DecompEndInput => IGzipResult.ShortInput,
            _ => IGzipResult.BadData,
        };
    }

    private static byte* AllocZeroed(nuint size)
    {
        var p = (byte*)NativeMemory.AlignedAlloc(size, Alignment);
        NativeMemory.Clear(p, size);
        return p;
    }

    private void ReleaseUnmanagedResources()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NativeMemory.AlignedFree(_deflate);
        NativeMemory.AlignedFree(_inflate);
        if (_levelBuf != null)
        {
            NativeMemory.AlignedFree(_levelBuf);
        }
    }

    public void Dispose()
    {
        ReleaseUnmanagedResources();
        GC.SuppressFinalize(this);
    }

    ~IGzipBinding()
    {
        ReleaseUnmanagedResources();
    }
}

internal static unsafe partial class NativeMethods
{
    public const string AssemblyName = "isal";
    public const string WindowsAssemblyName = "isa-l.dll";
    public const string OSXAssemblyName = "libisal.dylib";
    public const string UnixAssemblyName = "libisal.so";

    /// <summary>
    /// Highest libisal.so.N suffix probed on Linux. ISA-L has been .so.2 on every distro checked; the range
    /// exists because the digit is a per-library ABI version, not a convention.
    /// </summary>
    private const int MaxSoVersion = 9;

    static NativeMethods() => NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), DllImportResolver);

    private static nint _handle;

    /// <summary>
    /// Resolves the library up front so a missing one throws its install instructions from the constructor, before
    /// any native memory is allocated. NativeLibrary.Load does not consult the registered resolver, so call it directly.
    /// </summary>
    public static void EnsureLoaded()
    {
        if (_handle == 0)
        {
            _handle = DllImportResolver(AssemblyName, typeof(NativeMethods).Assembly, null);
        }
    }

    private static IntPtr DllImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != AssemblyName)
        {
            return IntPtr.Zero;
        }

        var libName = GetPlatformLibraryName();
        var assemblyLocation = assembly.Location;

        if (!string.IsNullOrEmpty(assemblyLocation))
        {
            var assemblyDir = Path.GetDirectoryName(assemblyLocation);
            if (assemblyDir != null)
            {
                // Bundled: runtimes/{rid}/native/ (standard NuGet layout for non-published builds)
                var runtimesPath = Path.Combine(assemblyDir, "runtimes", GetRuntimeIdentifier(), "native", libName);
                if (File.Exists(runtimesPath) && NativeLibrary.TryLoad(runtimesPath, out var runtimesHandle))
                {
                    return runtimesHandle;
                }

                // Bundled: directly next to the assembly (published apps)
                var bundledPath = Path.Combine(assemblyDir, libName);
                if (File.Exists(bundledPath) && NativeLibrary.TryLoad(bundledPath, out var bundledHandle))
                {
                    return bundledHandle;
                }
            }
        }

        // The loader's own search path, unversioned. This is what an install with the -dev package resolves on,
        // so it stays ahead of the versioned probe below.
        if (NativeLibrary.TryLoad(libName, assembly, searchPath, out var handle))
        {
            return handle;
        }

        // Linux ships libisal.so.N and only the -dev package adds the unversioned symlink the step above needs.
        // Probe versions by bare name so this still goes through the full loader search path (LD_LIBRARY_PATH,
        // /etc/ld.so.conf.d). Descending prefers the newest ABI.
        if (OperatingSystem.IsLinux())
        {
            for (var soVersion = MaxSoVersion; soVersion >= 0; soVersion--)
            {
                if (NativeLibrary.TryLoad($"{UnixAssemblyName}.{soVersion}", assembly, searchPath, out handle))
                {
                    return handle;
                }
            }
        }

        // Homebrew prefixes are off the default dyld search path.
        if (OperatingSystem.IsMacOS() &&
            (NativeLibrary.TryLoad($"/opt/homebrew/lib/{OSXAssemblyName}", out handle) ||
             NativeLibrary.TryLoad($"/usr/local/lib/{OSXAssemblyName}", out handle)))
        {
            return handle;
        }

        throw new DllNotFoundException(BuildNotFoundMessage());
    }

    private static string BuildNotFoundMessage()
    {
        if (OperatingSystem.IsLinux())
        {
            return $"""
                   Could not load ISA-L. Tried {UnixAssemblyName} and {UnixAssemblyName}.0 through {UnixAssemblyName}.{MaxSoVersion} on the loader path, and runtimes/{GetRuntimeIdentifier()}/native/ next to the assembly.

                   Install the runtime library. The -dev package is NOT required:
                     Debian/Ubuntu   sudo apt-get install -y libisal2
                     Fedora          sudo dnf install -y isa-l
                     RHEL/Rocky/Alma sudo dnf install -y epel-release && sudo dnf install -y isa-l
                     Alpine          apk add isa-l
                     Arch            sudo pacman -S isa-l
                   """;
        }

        if (OperatingSystem.IsMacOS())
        {
            return $"Could not load ISA-L. The bundled runtimes/{GetRuntimeIdentifier()}/native/{OSXAssemblyName} is missing; reinstall the package, or install it with: brew install isa-l";
        }

        return $"Could not load ISA-L. The bundled runtimes/{GetRuntimeIdentifier()}/native/{GetPlatformLibraryName()} is missing from the package.";
    }

    private static string GetPlatformLibraryName() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? WindowsAssemblyName :
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? OSXAssemblyName :
        UnixAssemblyName;

    private static string GetRuntimeIdentifier()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" :
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : "linux";

        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => "x64"
        };

        return $"{os}-{arch}";
    }

    [LibraryImport(AssemblyName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void isal_deflate_stateless_init(byte* stream);

    [LibraryImport(AssemblyName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int isal_deflate_stateless(byte* stream);

    [LibraryImport(AssemblyName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void isal_inflate_init(byte* state);

    [LibraryImport(AssemblyName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int isal_inflate_stateless(byte* state);
}
