# IGzip.Bindings

.NET 10+ bindings for [Intel ISA-L](https://github.com/intel/isa-l)'s igzip: one-shot zlib, raw deflate and
gzip compression over `Span<byte>`, with no streams and no managed allocations per call.

ISA-L is the fastest deflate encoder we measured for small payloads (gump and house-design packets): at level 1 it
compresses about 1.8x faster than libdeflate level 1 at a similar ratio, and its decoder is within about 10% of
libdeflate's.

## Usage

```csharp
using System.IO.Compression;

// One instance per thread; reuse it. Not thread safe.
using var igzip = new IGzipBinding(IGzipCompressionLevel.Level1, IGzipFormat.Zlib);

var packed = new byte[igzip.MaxPackSize(source.Length)];
var packedLength = igzip.Pack(packed, source);          // 0 if the destination is too small

var result = igzip.Unpack(dest, packed.AsSpan(0, packedLength), out var length);
```

`IGzipFormat.Zlib` produces exactly what zlib's `compress()`/`uncompress()` read and write (2-byte header,
Adler-32 trailer). `Deflate` is raw RFC 1951 and `Gzip` is RFC 1952.

Levels: `Level0` is fastest but uses static Huffman tables and compresses much worse; `Level1` is the right default
for small payloads; `Level3` is slower for a slightly better ratio.

## Native library

| Platform | Source |
|---|---|
| Windows x64 | bundled (`runtimes/win-x64/native/isa-l.dll`) |
| macOS arm64 | bundled (`runtimes/osx-arm64/native/libisal.dylib`) |
| Linux | the distro package, below |

The bundled binaries are built by `.github/workflows/natives.yml` from a pinned ISA-L release tag; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

On Linux install the runtime package. The `-dev` package is **not** required: the binding probes the versioned
`libisal.so.N` itself.

```sh
sudo apt-get install -y libisal2                                    # Debian / Ubuntu
sudo dnf install -y isa-l                                           # Fedora
sudo dnf install -y epel-release && sudo dnf install -y isa-l       # RHEL / Rocky / Alma
apk add isa-l                                                       # Alpine
sudo pacman -S isa-l                                                # Arch
```

Distro builds are fully optimized: ISA-L picks its SSE/AVX2/AVX-512 (or NEON) kernels at runtime from the CPU.

The binding drives ISA-L's stateless API through fixed struct offsets. Those layouts are identical in every ISA-L
release from 2.28 through 2.32.1 on Windows, glibc and musl, and CI re-checks them against each distro's headers.

## License

IGzip.Bindings is licensed under the [BSD 2-Clause License](LICENSE). The bundled ISA-L binaries are BSD-3-Clause;
see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
