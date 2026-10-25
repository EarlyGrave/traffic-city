namespace Sim.Core.Util;

/// <summary>PCG32 XSH-RR (pcg-random.org): 64-бит состояние, нечётный 64-бит инкремент (поток).</summary>
public struct Pcg32
{
    private const ulong Mult = 6364136223846793005UL;

    private ulong _state;
    private ulong _inc; // всегда нечётный

    /// <summary>pcg32_srandom_r: inc = (stream &lt;&lt; 1) | 1; state = 0; step; state += seed; step.</summary>
    public static Pcg32 Create(ulong seed, ulong stream)
    {
        var rng = new Pcg32 { _state = 0UL, _inc = (stream << 1) | 1UL };
        rng.NextUInt();
        rng._state += seed;
        rng.NextUInt();
        return rng;
    }

    /// <summary>Подпоток подсистемы от общего seed сценария.</summary>
    public static Pcg32 ForStream(ulong seed, RngStream stream) => Create(seed, (ulong)stream);

    /// <summary>Восстановление из сейва: состояние и инкремент как были (инкремент обязан быть нечётным).</summary>
    public static Pcg32 FromState(ulong state, ulong inc) => new() { _state = state, _inc = inc };

    public ulong State => _state;

    public ulong Inc => _inc;

    /// <summary>pcg32_random_r.</summary>
    public uint NextUInt()
    {
        ulong old = _state;
        _state = (old * Mult) + _inc;
        uint xorshifted = (uint)(((old >> 18) ^ old) >> 27);
        int rot = (int)(old >> 59);
        return (xorshifted >> rot) | (xorshifted << ((-rot) & 31));
    }

    /// <summary>[0, bound) умножением-сдвигом; смещение &lt;= 2^-32, принято ради портируемости.</summary>
    public uint NextBelow(uint bound) => (uint)(((ulong)NextUInt() * bound) >> 32);

    public int NextInt(int minInclusive, int maxExclusive) =>
        minInclusive + (int)NextBelow((uint)(maxExclusive - minInclusive));

    /// <summary>[0, 1), 24 бита мантиссы.</summary>
    public float NextFloat01() => (NextUInt() >> 8) * (1.0f / 16777216.0f);

    public float NextRange(float min, float max) => min + ((max - min) * NextFloat01());
}
