using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Sim.Core.Util;

/// <summary>
/// XXH64 (seed 0 по умолчанию), собственная реализация без NuGet (ADR-0012: контрольные суммы сейва; та же функция есть в Rust).
/// Потоковая: <see cref="Append"/> любыми кусками даёт тот же результат, что и одиночный <see cref="Hash"/>. Не аллоцирует.
/// </summary>
internal struct XxHash64
{
    private const ulong P1 = 11400714785074694791UL;
    private const ulong P2 = 14029467366897019727UL;
    private const ulong P3 = 1609587929392839161UL;
    private const ulong P4 = 9650029242287828579UL;
    private const ulong P5 = 2870177450012600261UL;

    private ulong _v1, _v2, _v3, _v4;
    private ulong _seed;
    private ulong _total;
    private int _buffered;
    private Buf32 _buf;

    [InlineArray(32)]
    private struct Buf32
    {
        private byte _e;
    }

    public static XxHash64 Create(ulong seed = 0)
    {
        var s = new XxHash64
        {
            _seed = seed,
            _v1 = unchecked(seed + P1 + P2),
            _v2 = unchecked(seed + P2),
            _v3 = seed,
            _v4 = unchecked(seed - P1),
        };
        return s;
    }

    /// <summary>Хэш куска одним вызовом.</summary>
    public static ulong Hash(ReadOnlySpan<byte> data, ulong seed = 0)
    {
        var s = Create(seed);
        s.Append(data);
        return s.Value;
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        _total += (ulong)data.Length;

        if (_buffered > 0)
        {
            int take = Math.Min(32 - _buffered, data.Length);
            Span<byte> buf = _buf;
            data.Slice(0, take).CopyTo(buf.Slice(_buffered));
            _buffered += take;
            data = data.Slice(take);
            if (_buffered < 32)
            {
                return;
            }

            Stripe(buf);
            _buffered = 0;
        }

        while (data.Length >= 32)
        {
            Stripe(data);
            data = data.Slice(32);
        }

        if (data.Length > 0)
        {
            Span<byte> buf = _buf;
            data.CopyTo(buf);
            _buffered = data.Length;
        }
    }

    /// <summary>Хэш всех добавленных байт; состояние не меняется (можно продолжать добавлять).</summary>
    public readonly ulong Value
    {
        get
        {
            ulong h;
            if (_total >= 32)
            {
                h = BitOperations.RotateLeft(_v1, 1) + BitOperations.RotateLeft(_v2, 7)
                    + BitOperations.RotateLeft(_v3, 12) + BitOperations.RotateLeft(_v4, 18);
                h = Merge(h, _v1);
                h = Merge(h, _v2);
                h = Merge(h, _v3);
                h = Merge(h, _v4);
            }
            else
            {
                h = unchecked(_seed + P5);
            }

            h = unchecked(h + _total);

            ReadOnlySpan<byte> tail = ((ReadOnlySpan<byte>)_buf).Slice(0, _buffered);
            while (tail.Length >= 8)
            {
                h ^= Round(0, BinaryPrimitives.ReadUInt64LittleEndian(tail));
                h = unchecked(BitOperations.RotateLeft(h, 27) * P1 + P4);
                tail = tail.Slice(8);
            }

            if (tail.Length >= 4)
            {
                h ^= unchecked(BinaryPrimitives.ReadUInt32LittleEndian(tail) * P1);
                h = unchecked(BitOperations.RotateLeft(h, 23) * P2 + P3);
                tail = tail.Slice(4);
            }

            for (int i = 0; i < tail.Length; i++)
            {
                h ^= unchecked(tail[i] * P5);
                h = unchecked(BitOperations.RotateLeft(h, 11) * P1);
            }

            h ^= h >> 33;
            h = unchecked(h * P2);
            h ^= h >> 29;
            h = unchecked(h * P3);
            h ^= h >> 32;
            return h;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Round(ulong acc, ulong input) =>
        unchecked(BitOperations.RotateLeft(acc + input * P2, 31) * P1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Merge(ulong h, ulong v) => unchecked((h ^ Round(0, v)) * P1 + P4);

    private void Stripe(ReadOnlySpan<byte> s)
    {
        _v1 = Round(_v1, BinaryPrimitives.ReadUInt64LittleEndian(s));
        _v2 = Round(_v2, BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(8)));
        _v3 = Round(_v3, BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(16)));
        _v4 = Round(_v4, BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(24)));
    }
}
