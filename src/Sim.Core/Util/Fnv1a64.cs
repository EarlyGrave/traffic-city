using System;

namespace Sim.Core.Util;

/// <summary>FNV-1a 64. Многобайтные значения — little-endian; float — по битовому образу.</summary>
public struct Fnv1a64
{
    public const ulong Offset = 14695981039346656037UL;
    public const ulong Prime = 1099511628211UL;

    private ulong _h;

    public static Fnv1a64 Create() => new() { _h = Offset };

    public readonly ulong Value => _h;

    public void Add(byte b)
    {
        _h = (_h ^ b) * Prime;
    }

    public void Add(int v)
    {
        Add((byte)v);
        Add((byte)(v >> 8));
        Add((byte)(v >> 16));
        Add((byte)(v >> 24));
    }

    public void Add(uint v) => Add((int)v);

    public void Add(long v)
    {
        Add((int)v);
        Add((int)(v >> 32));
    }

    public void Add(ulong v) => Add((long)v);

    public void Add(float v) => Add(BitConverter.SingleToInt32Bits(v));

    public void Add(ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            Add(bytes[i]);
        }
    }
}
