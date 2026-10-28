using System;
using System.Buffers.Binary;
using System.Globalization;

namespace Sim.Scenarios.Mods.Gltf;

/// <summary>Разобранный контейнер GLB: JSON-чанк и необязательный BIN-чанк (срезы исходного массива, без копий).</summary>
internal readonly record struct GlbChunks(ReadOnlyMemory<byte> Json, ReadOnlyMemory<byte> Bin, bool HasBin);

/// <summary>Чтение контейнера GLB (modding.md §4.2): магия <c>glTF</c>, версия 2, согласованные длины, чанки JSON + BIN.</summary>
internal static class GlbReader
{
    private const uint Magic = 0x46546C67;
    private const uint ChunkJson = 0x4E4F534A;
    private const uint ChunkBin = 0x004E4942;
    private const int HeaderSize = 12;
    private const int ChunkHeaderSize = 8;

    public static bool TryRead(byte[] data, out GlbChunks chunks, out string error)
    {
        chunks = default;
        if (data.Length < HeaderSize + ChunkHeaderSize)
        {
            error = "file shorter than GLB header";
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic)
        {
            error = "bad GLB magic";
            return false;
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        if (version != 2)
        {
            error = $"unsupported GLB version {version.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        uint total = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
        if (total != (uint)data.Length)
        {
            error = $"header length {total.ToString(CultureInfo.InvariantCulture)} differs from file length {data.Length.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        int pos = HeaderSize;
        bool haveJson = false;
        ReadOnlyMemory<byte> json = default;
        ReadOnlyMemory<byte> bin = default;
        bool haveBin = false;
        int index = 0;
        while (pos < data.Length)
        {
            if (data.Length - pos < ChunkHeaderSize)
            {
                error = "truncated chunk header";
                return false;
            }

            uint len = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4));
            pos += ChunkHeaderSize;
            if (len > (uint)(data.Length - pos))
            {
                error = $"chunk {index.ToString(CultureInfo.InvariantCulture)} length {len.ToString(CultureInfo.InvariantCulture)} exceeds file";
                return false;
            }

            if (index == 0)
            {
                if (type != ChunkJson)
                {
                    error = "first chunk is not JSON";
                    return false;
                }

                json = new ReadOnlyMemory<byte>(data, pos, (int)len);
                haveJson = true;
            }
            else if (type == ChunkBin && !haveBin)
            {
                bin = new ReadOnlyMemory<byte>(data, pos, (int)len);
                haveBin = true;
            }

            // Чанки неизвестного типа игнорируются (спецификация glTF 2.0, 4.4.3.3).
            pos += (int)len;
            index++;
        }

        if (!haveJson)
        {
            error = "no JSON chunk";
            return false;
        }

        chunks = new GlbChunks(json, bin, haveBin);
        error = string.Empty;
        return true;
    }
}
