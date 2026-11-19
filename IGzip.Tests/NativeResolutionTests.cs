using System.IO.Compression;

namespace IGzip.Tests;

/// <summary>
/// Runs a fact only when ISAL_TEST_MODE matches, otherwise reports it as skipped. xunit v2 has no runtime skip,
/// so the decision is made at discovery time.
/// </summary>
public sealed class NativeModeFactAttribute : FactAttribute
{
    public const string ModeVariable = "ISAL_TEST_MODE";

    public NativeModeFactAttribute(string mode, string requires)
    {
        if (Environment.GetEnvironmentVariable(ModeVariable) != mode)
        {
            Skip = $"Requires {ModeVariable}={mode} ({requires}).";
        }
    }
}

/// <summary>
/// Native resolution depends on which packages exist on the machine, so these only run inside the CI containers
/// that control that. On Windows and macOS the package bundles the library and resolution never reaches the
/// versioned-SONAME path.
/// </summary>
public class NativeResolutionTests
{
    /// <summary>Only libisal.so.2 is installed; DllImport never asks for a versioned SONAME on its own.</summary>
    [NativeModeFact("runtime-only", "container with the runtime package and no -dev")]
    public void RuntimePackageOnly_ResolvesViaVersionedSoname() => AssertRoundTrips();

    /// <summary>With the -dev symlink present, resolution must still succeed on the plain-name step.</summary>
    [NativeModeFact("dev", "container with the -dev package")]
    public void DevPackageInstalled_StillResolves() => AssertRoundTrips();

    [NativeModeFact("missing", "container with no ISA-L at all")]
    public void NoNativeLibrary_ThrowsWithRuntimePackageInstructions()
    {
        var exception = Record.Exception(() => new IGzipBinding());

        var dllNotFound = Unwrap<DllNotFoundException>(exception);
        Assert.NotNull(dllNotFound);

        Assert.Contains("libisal2", dllNotFound.Message, StringComparison.Ordinal);
        Assert.Contains("libisal.so.0 through libisal.so.9", dllNotFound.Message, StringComparison.Ordinal);
        Assert.Contains("-dev", dllNotFound.Message, StringComparison.Ordinal);
    }

    private static void AssertRoundTrips()
    {
        using var binding = new IGzipBinding();

        var source = "the quick brown fox jumps over the lazy dog"u8;
        var packed = new byte[binding.MaxPackSize(source.Length)];
        var packedLength = binding.Pack(packed, source);
        Assert.True(packedLength > 0);

        var unpacked = new byte[source.Length];
        var result = binding.Unpack(unpacked, packed.AsSpan(0, packedLength), out var unpackedLength);

        Assert.Equal(IGzipResult.Success, result);
        Assert.Equal(source.Length, unpackedLength);
        Assert.True(source.SequenceEqual(unpacked));
    }

    private static T? Unwrap<T>(Exception? exception) where T : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }
}
