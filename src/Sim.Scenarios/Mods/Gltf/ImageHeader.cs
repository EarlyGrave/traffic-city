using System;
using System.Buffers.Binary;
using System.IO;

namespace Sim.Scenarios.Mods.Gltf;

/// <summary>Формат изображения по заголовку.</summary>
internal enum ImageFormat
{
    Png,
    Jpeg,
    Ktx2,
}

/// <summary>Размеры изображения из заголовка без декодирования (modding.md §4.2): PNG (IHDR), JPEG (SOF), KTX2.</summary>
internal static class ImageHeader
{
    private static readonly byte[] _pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] _ktx2Identifier = [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Поток должен поддерживать <c>Seek</c>. <c>false</c> — заголовок не распознан или усечён.</summary>
    public static bool TryRead(Stream s, out ImageFormat format, out long width, out long height)
    {
        format = default;
        width = 0;
        height = 0;
        Span<byte> head = stackalloc byte[12];
        int n = ReadUpTo(s, head);
        if (n >= 8 && head[..8].SequenceEqual(_pngSignature))
        {
            format = ImageFormat.Png;
            s.Seek(8 - n, SeekOrigin.Current);
            Span<byte> ihdr = stackalloc byte[16];
            if (ReadUpTo(s, ihdr) < 16 || ihdr[4] != (byte)'I' || ihdr[5] != (byte)'H' || ihdr[6] != (byte)'D' || ihdr[7] != (byte)'R')
            {
                return false;
            }

            width = BinaryPrimitives.ReadUInt32BigEndian(ihdr[8..]);
            height = BinaryPrimitives.ReadUInt32BigEndian(ihdr[12..]);
            return true;
        }

        if (n >= 12 && head.SequenceEqual(_ktx2Identifier))
        {
            format = ImageFormat.Ktx2;
            Span<byte> rest = stackalloc byte[16];
            if (ReadUpTo(s, rest) < 16)
            {
                return false;
            }

            // vkFormat (4), typeSize (4), pixelWidth (4), pixelHeight (4)
            width = BinaryPrimitives.ReadUInt32LittleEndian(rest[8..]);
            height = BinaryPrimitives.ReadUInt32LittleEndian(rest[12..]);
            return true;
        }

        if (n >= 2 && head[0] == 0xFF && head[1] == 0xD8)
        {
            format = ImageFormat.Jpeg;
            s.Seek(2 - n, SeekOrigin.Current);
            return TryReadJpeg(s, out width, out height);
        }

        return false;
    }

    private static bool TryReadJpeg(Stream s, out long width, out long height)
    {
        width = 0;
        height = 0;
        Span<byte> b = stackalloc byte[7];
        while (true)
        {
            int c = s.ReadByte();
            if (c < 0)
            {
                return false;
            }

            if (c != 0xFF)
            {
                continue;
            }

            int marker;
            do
            {
                marker = s.ReadByte();
            }
            while (marker == 0xFF);
            if (marker < 0)
            {
                return false;
            }

            if (marker == 0 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD8))
            {
                continue;
            }

            if (marker == 0xD9 || marker == 0xDA)
            {
                return false;
            }

            bool sof = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
            if (sof)
            {
                // длина (2) уже входит в эти 7 байт: len(2), precision(1), height(2), width(2)
                if (ReadUpTo(s, b) < 7)
                {
                    return false;
                }

                height = BinaryPrimitives.ReadUInt16BigEndian(b[3..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(b[5..]);
                return true;
            }

            if (ReadUpTo(s, b[..2]) < 2)
            {
                return false;
            }

            int len = BinaryPrimitives.ReadUInt16BigEndian(b);
            if (len < 2)
            {
                return false;
            }

            s.Seek(len - 2, SeekOrigin.Current);
        }
    }

    private static int ReadUpTo(Stream s, Span<byte> buf)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int r = s.Read(buf[total..]);
            if (r <= 0)
            {
                break;
            }

            total += r;
        }

        return total;
    }
}
