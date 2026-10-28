using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.IO.Hashing;

namespace Sim.Host;

/// <summary>
/// Конверт файла <c>.tcsave</c> (save-format.md §7): заголовок + deflate над сырым контейнером ядра. Ядро сжатия не делает (System.IO запрещён,
/// а канонический - именно сырой контейнер: сжатые байты C# и Rust не совпадут).
/// <code>
///  0 magic "TCSZ\r\n\x1a\n" (8) | 8 u16 envelope_version = 1, u16 codec (0 нет, 1 deflate, 2 zstd - резерв), u32 reserved = 0
/// 16 u64 raw_length | 24 u64 xxh64(сырой контейнер) | 32 копия 64-байтного заголовка контейнера | 96 сжатый поток
/// </code>
/// </summary>
public static class SaveEnvelope
{
    public const int EnvelopeHeaderSize = 96;
    public const int ContainerHeaderSize = 64;
    public const int CodecNone = 0;
    public const int CodecDeflate = 1;

    private static ReadOnlySpan<byte> Magic => "TCSZ\r\n\x1a\n"u8;

    /// <summary>Файл начинается с магии конверта.</summary>
    public static bool IsEnvelope(ReadOnlySpan<byte> file) => file.Length >= 8 && file[..8].SequenceEqual(Magic);

    /// <summary>Оборачивает сырой контейнер (первые 64 байта обязаны быть его заголовком). <paramref name="codec"/>: 0 без сжатия, 1 deflate.</summary>
    public static byte[] Pack(ReadOnlySpan<byte> container, int codec = CodecDeflate)
    {
        if (container.Length < ContainerHeaderSize)
        {
            throw new ArgumentException("container is shorter than its header", nameof(container));
        }

        if (codec is not (CodecNone or CodecDeflate))
        {
            throw new ArgumentOutOfRangeException(nameof(codec));
        }

        using var ms = new MemoryStream(container.Length / 2 + EnvelopeHeaderSize);
        ms.SetLength(EnvelopeHeaderSize);
        ms.Position = EnvelopeHeaderSize;
        if (codec == CodecDeflate)
        {
            using var z = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true);
            z.Write(container);
        }
        else
        {
            ms.Write(container);
        }

        byte[] file = ms.ToArray();
        Magic.CopyTo(file);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(10), (ushort)codec);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(16), (ulong)container.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(24), XxHash64.HashToUInt64(container));
        container[..ContainerHeaderSize].CopyTo(file.AsSpan(32));
        return file;
    }

    /// <summary>Снимает конверт. Любая порча (магия, версия, кодек, длина, поток, xxh64, заголовок-копия) - false и текст причины.</summary>
    public static bool TryUnpack(ReadOnlySpan<byte> file, out byte[] container, out string error)
    {
        container = Array.Empty<byte>();
        error = string.Empty;
        if (!IsEnvelope(file))
        {
            error = "not a .tcsave envelope (bad magic)";
            return false;
        }

        if (file.Length < EnvelopeHeaderSize)
        {
            error = "truncated envelope header";
            return false;
        }

        int version = BinaryPrimitives.ReadUInt16LittleEndian(file[8..]);
        int codec = BinaryPrimitives.ReadUInt16LittleEndian(file[10..]);
        ulong rawLength = BinaryPrimitives.ReadUInt64LittleEndian(file[16..]);
        ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(file[24..]);
        if (version != 1 || BinaryPrimitives.ReadUInt32LittleEndian(file[12..]) != 0)
        {
            error = $"unsupported envelope version {version}";
            return false;
        }

        if (codec is not (CodecNone or CodecDeflate))
        {
            error = $"unsupported codec {codec}";
            return false;
        }

        // Заявленная длина ограничивает распаковку (защита от бомбы): deflate не раздувает больше, чем ~1032:1.
        if (rawLength < ContainerHeaderSize || rawLength > int.MaxValue || (codec == CodecNone && rawLength != (ulong)(file.Length - EnvelopeHeaderSize))
            || (codec == CodecDeflate && rawLength / 1032 > (ulong)file.Length))
        {
            error = "bad raw_length";
            return false;
        }

        byte[] raw = new byte[(int)rawLength];
        ReadOnlySpan<byte> body = file[EnvelopeHeaderSize..];
        if (codec == CodecNone)
        {
            body.CopyTo(raw);
        }
        else
        {
            try
            {
                using var src = new MemoryStream(body.ToArray(), writable: false);
                using var z = new DeflateStream(src, CompressionMode.Decompress);
                int got = 0;
                while (got < raw.Length)
                {
                    int n = z.Read(raw, got, raw.Length - got);
                    if (n == 0)
                    {
                        break;
                    }

                    got += n;
                }

                if (got != raw.Length || z.ReadByte() != -1)
                {
                    error = "decompressed length differs from raw_length";
                    return false;
                }
            }
            catch (InvalidDataException)
            {
                error = "corrupt deflate stream";
                return false;
            }
        }

        if (XxHash64.HashToUInt64(raw) != hash)
        {
            error = "xxh64 of the container does not match";
            return false;
        }

        if (!file.Slice(32, ContainerHeaderSize).SequenceEqual(raw.AsSpan(0, ContainerHeaderSize)))
        {
            error = "header copy differs from the container header";
            return false;
        }

        container = raw;
        return true;
    }

    /// <summary>Читает файл: конверт распаковывается, иначе содержимое считается сырым контейнером (как пишет <c>save --raw</c>).</summary>
    public static byte[] ReadContainer(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        if (!IsEnvelope(file))
        {
            return file;
        }

        return TryUnpack(file, out byte[] raw, out string err) ? raw : throw new InvalidDataException($"{path}: {err}");
    }
}
