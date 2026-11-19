using System.IO.Compression;
using System.Text;

namespace IGzip.Tests;

public class IGzipTests
{
    public static TheoryData<IGzipCompressionLevel, IGzipFormat> LevelsAndFormats()
    {
        var data = new TheoryData<IGzipCompressionLevel, IGzipFormat>();
        foreach (var level in Enum.GetValues<IGzipCompressionLevel>())
        {
            foreach (var format in Enum.GetValues<IGzipFormat>())
            {
                data.Add(level, format);
            }
        }

        return data;
    }

    private static readonly int[] Sizes = [0, 1, 37, 1000, 0xFFFF, 0x10000, 200_000];

    private static byte[] Text(int length)
    {
        var sb = new StringBuilder();
        var rng = new Random(length);
        while (sb.Length < length)
        {
            sb.Append($"{{ button {rng.Next(10, 400)} {rng.Next(10, 400)} 4005 4007 1 0 {rng.Next(100)} }}");
            sb.Append($"{{ xmfhtmlgumpcolor 50 {rng.Next(400)} 150 18 {rng.Next(1011000, 1080000)} 0 0 32767 }}");
        }

        return Encoding.ASCII.GetBytes(sb.ToString(0, length));
    }

    private static byte[] RandomBytes(int length)
    {
        var data = new byte[length];
        new Random(length + 1).NextBytes(data);
        return data;
    }

    [Theory]
    [MemberData(nameof(LevelsAndFormats))]
    public void RoundTrips_AndStandardDecodersAgree(IGzipCompressionLevel level, IGzipFormat format)
    {
        using var binding = new IGzipBinding(level, format);

        foreach (var size in Sizes)
        {
            foreach (var input in new[] { Text(size), RandomBytes(size) })
            {
                var packed = new byte[binding.MaxPackSize(input.Length)];
                var packedLength = binding.Pack(packed, input);
                Assert.True(packedLength > 0, $"{level} {format} failed to pack {input.Length} bytes");

                var unpacked = new byte[input.Length];
                Assert.Equal(IGzipResult.Success, binding.Unpack(unpacked, packed.AsSpan(0, packedLength), out var n));
                Assert.Equal(input.Length, n);
                Assert.True(input.AsSpan().SequenceEqual(unpacked));

                // The framing is the standard one, not something only ISA-L reads back.
                Assert.True(input.AsSpan().SequenceEqual(DecodeWithDotNet(format, packed.AsSpan(0, packedLength))));
            }
        }
    }

    [Theory]
    [InlineData(IGzipFormat.Deflate)]
    [InlineData(IGzipFormat.Gzip)]
    [InlineData(IGzipFormat.Zlib)]
    public void Unpack_ReadsStreamsFromOtherEncoders(IGzipFormat format)
    {
        using var binding = new IGzipBinding(format: format);

        foreach (var size in Sizes)
        {
            var input = Text(size);
            var packed = EncodeWithDotNet(format, input);

            // .NET 10's streams write nothing at all for empty input, which is not a stream to decode.
            if (packed.Length == 0)
            {
                continue;
            }

            var unpacked = new byte[input.Length];
            Assert.Equal(IGzipResult.Success, binding.Unpack(unpacked, packed, out var n));
            Assert.Equal(input.Length, n);
            Assert.True(input.AsSpan().SequenceEqual(unpacked));
        }
    }

    [Fact]
    public void Zlib_UsesTheStandardHeaderAndAdler32Trailer()
    {
        using var binding = new IGzipBinding();
        var input = Text(5000);
        var packed = new byte[binding.MaxPackSize(input.Length)];
        var n = binding.Pack(packed, input);

        Assert.Equal(0x78, packed[0]);
        Assert.Equal(0, (packed[0] * 256 + packed[1]) % 31);

        uint a = 1, b = 0;
        foreach (var x in input)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }

        var trailer = (uint)(packed[n - 4] << 24 | packed[n - 3] << 16 | packed[n - 2] << 8 | packed[n - 1]);
        Assert.Equal((b << 16) | a, trailer);
    }

    [Fact]
    public void Pack_ReturnsZeroWithoutOverrunning_WhenDestinationIsTooSmall()
    {
        foreach (var level in Enum.GetValues<IGzipCompressionLevel>())
        {
            using var binding = new IGzipBinding(level);
            var input = Text(20_000);
            var full = binding.Pack(new byte[binding.MaxPackSize(input.Length)], input);

            var guarded = new byte[full + 64];
            guarded.AsSpan(full - 1).Fill(0xCC);

            Assert.Equal(0, binding.Pack(guarded.AsSpan(0, full - 1), input));
            Assert.Equal(-1, guarded.AsSpan(full - 1).IndexOfAnyExcept((byte)0xCC));

            // The instance stays usable after a failed call.
            Assert.Equal(full, binding.Pack(new byte[binding.MaxPackSize(input.Length)], input));
        }
    }

    [Fact]
    public void Unpack_ReportsEachFailure()
    {
        using var binding = new IGzipBinding();
        var input = Text(10_000);
        var packed = new byte[binding.MaxPackSize(input.Length)];
        var n = binding.Pack(packed, input);
        var stream = packed.AsSpan(0, n).ToArray();
        var dest = new byte[input.Length];

        Assert.Equal(IGzipResult.InsufficientSpace, binding.Unpack(dest.AsSpan(0, input.Length - 1), stream, out _));
        Assert.Equal(IGzipResult.ShortInput, binding.Unpack(dest, stream.AsSpan(0, n / 2), out _));

        var badChecksum = stream.ToArray();
        badChecksum[^1] ^= 0xFF;
        Assert.Equal(IGzipResult.BadData, binding.Unpack(dest, badChecksum, out _));

        var badHeader = stream.ToArray();
        badHeader[0] = 0x00;
        Assert.Equal(IGzipResult.BadData, binding.Unpack(dest, badHeader, out var length));
        Assert.Equal(0, length);

        Assert.Equal(IGzipResult.Success, binding.Unpack(dest, stream, out length));
        Assert.Equal(input.Length, length);
    }

    [Fact]
    public void Dispose_IsIdempotent_AndBlocksFurtherUse()
    {
        var binding = new IGzipBinding();
        binding.Dispose();
        binding.Dispose();

        Assert.Throws<ObjectDisposedException>(() => binding.Pack(new byte[64], "x"u8));
    }

    private static byte[] DecodeWithDotNet(IGzipFormat format, ReadOnlySpan<byte> packed)
    {
        using var input = new MemoryStream(packed.ToArray());
        using Stream decoder = format switch
        {
            IGzipFormat.Zlib => new ZLibStream(input, CompressionMode.Decompress),
            IGzipFormat.Gzip => new GZipStream(input, CompressionMode.Decompress),
            _ => new DeflateStream(input, CompressionMode.Decompress),
        };
        using var output = new MemoryStream();
        decoder.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] EncodeWithDotNet(IGzipFormat format, byte[] data)
    {
        using var output = new MemoryStream();
        using (Stream encoder = format switch
               {
                   IGzipFormat.Zlib => new ZLibStream(output, CompressionLevel.Optimal),
                   IGzipFormat.Gzip => new GZipStream(output, CompressionLevel.Optimal),
                   _ => new DeflateStream(output, CompressionLevel.Optimal),
               })
        {
            encoder.Write(data);
        }

        return output.ToArray();
    }
}
