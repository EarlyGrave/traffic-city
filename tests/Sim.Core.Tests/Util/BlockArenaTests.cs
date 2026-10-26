using System;
using System.Collections.Generic;
using Sim.Core.Util;

namespace Sim.Core.Tests.Util;

/// <summary>Арена блоков (player-tools.md §3 п. 2): лестница ёмкостей, LIFO-free-list по классам, рост на месте, потери, состояние для сейва.</summary>
public sealed class BlockArenaTests
{
    [Fact]
    public void Ladder_IsMonotoneAndCapForIsAStep()
    {
        int prev = 0;
        for (int n = 1; n <= 5000; n++)
        {
            int cap = BlockArena.CapFor(n);
            Assert.True(cap >= n && cap >= prev, $"CapFor({n}) = {cap}");
            Assert.True(BlockArena.ClassOf(cap) >= 0, $"CapFor({n}) = {cap} не ступенька");
            Assert.Equal(cap, BlockArena.CapFor(cap));
            prev = cap;
        }

        for (int n = 1; n <= 8; n++)
        {
            Assert.Equal(n, BlockArena.CapFor(n));   // малые блоки (полосы направления, рукава) без запаса
        }

        Assert.Equal(0, BlockArena.CapFor(0));
        Assert.Equal(10, BlockArena.CapFor(9));
        Assert.Equal(64, BlockArena.CapFor(57));
    }

    [Fact]
    public void ExactBlocks_AreTightAtTail_AndNotGrowable()
    {
        var a = new BlockArena(100);
        int b0 = a.AllocExact(5);
        int b1 = a.AllocExact(7);
        Assert.Equal(0, b0);
        Assert.Equal(5, b1);
        Assert.Equal(12, a.Tail);
        Assert.False(a.IsLadder(b0));
        Assert.Equal(5, a.CapacityOf(b0, 5));
        Assert.False(a.CanGrow(b0, 5, 6));
        Assert.True(a.CanGrow(b0, 5, 4));
        Assert.Equal(-1, a.AllocExact(89));
        Assert.Equal(12, a.Tail);
    }

    [Fact]
    public void LadderBlock_HasSpareAndGrowsInPlaceWithinStep()
    {
        var a = new BlockArena(200);
        int b = a.Alloc(9);
        Assert.True(a.IsLadder(b));
        Assert.Equal(10, a.CapacityOf(b, 9));
        Assert.Equal(10, a.Tail);
        Assert.True(a.CanGrow(b, 9, 10));
        Assert.False(a.CanGrow(b, 9, 11));
        Assert.Equal(b + 10, a.Alloc(1));   // следующий блок сразу за запасом
    }

    [Fact]
    public void Free_LifoPerClass_AndExactBlockSplitsIntoSteps()
    {
        var a = new BlockArena(500);
        int x = a.Alloc(4);
        int y = a.Alloc(4);
        int z = a.Alloc(4);
        a.Free(x, 4);
        a.Free(z, 4);
        Assert.Equal(2, a.FreeBlockCount);
        Assert.Equal(z, a.Alloc(4));   // LIFO: сначала последний освобождённый класса
        Assert.Equal(x, a.Alloc(4));
        Assert.Equal(0, a.FreeBlockCount);
        Assert.True(a.IsLadder(z) && a.IsLadder(x));
        a.Free(y, 4);

        // Блок без запаса из 11 элементов (10 + 1) раскладывается жадно: ступеньки 10 и 1.
        var e = new BlockArena(100);
        int blk = e.AllocExact(11);
        e.Free(blk, 11);
        Assert.Equal(2, e.FreeBlockCount);
        Assert.Equal(blk, e.Alloc(10));
        Assert.Equal(blk + 10, e.Alloc(1));
        Assert.Equal(11, e.Tail);
    }

    [Fact]
    public void NoSpace_ReportsMinusOne_AndCanAllocAgrees()
    {
        var a = new BlockArena(10);
        Assert.True(a.CanAlloc(9));
        Assert.Equal(0, a.Alloc(9));
        Assert.False(a.CanAlloc(2));
        Assert.Equal(-1, a.Alloc(2));
        Assert.Equal(10, a.Tail);   // отказ ничего не меняет
        Assert.Equal(10, a.AllocExact(0));   // пустой блок - хвост, ничего не занимает
    }

    [Fact]
    public void EntryPoolExhausted_LeaksHoleInsteadOfFailing()
    {
        var a = new BlockArena(100, entryCapacity: 2);
        int[] b = { a.Alloc(2), a.Alloc(2), a.Alloc(2), a.Alloc(2) };
        foreach (int f in b)
        {
            a.Free(f, 2);
        }

        Assert.Equal(2, a.FreeBlockCount);
        Assert.Equal(4, a.LeakedElements);   // две дыры по 2 элемента потеряны, освобождение не провалилось
    }

    [Fact]
    public void Fuzz_BlocksNeverOverlap_AndHolesAreExactlyTheFreed()
    {
        for (int seed = 1; seed <= 20; seed++)
        {
            var rng = new Random(seed);
            var a = new BlockArena(4000);
            var live = new List<(int First, int N)>();
            var owner = new int[4000];
            Array.Fill(owner, -1);
            int id = 0;
            for (int step = 0; step < 600; step++)
            {
                if (live.Count > 0 && rng.Next(100) < 45)
                {
                    int k = rng.Next(live.Count);
                    (int first, int n) = live[k];
                    live.RemoveAt(k);
                    int cap = a.CapacityOf(first, n);
                    for (int i = first; i < first + cap; i++)
                    {
                        owner[i] = -1;
                    }

                    a.Free(first, n);
                }
                else
                {
                    int n = 1 + rng.Next(rng.Next(100) < 80 ? 12 : 120);
                    bool exact = rng.Next(4) == 0 && a.FreeBlockCount == 0;
                    int first = exact ? a.AllocExact(n) : a.Alloc(n);
                    if (first < 0)
                    {
                        continue;
                    }

                    int cap = a.CapacityOf(first, n);
                    for (int i = first; i < first + cap; i++)
                    {
                        Assert.True(owner[i] == -1, $"seed {seed}: элемент {i} занят блоком {owner[i]}");
                        owner[i] = id;
                    }

                    live.Add((first, n));
                    id++;
                }
            }

            // Все дыры лежат на свободных элементах (внутри хвоста) и не пересекаются между собой.
            var packed = new ulong[a.FreeBlockCount];
            a.CopyFree(packed);
            var seen = new bool[4000];
            long holes = 0;
            foreach (ulong p in packed)
            {
                int first = (int)(uint)p;
                int cap = BlockArena.Steps[(int)(p >> 32)];
                for (int i = first; i < first + cap; i++)
                {
                    Assert.True(owner[i] == -1 && !seen[i], $"seed {seed}: дыра на занятом/повторном элементе {i}");
                    seen[i] = true;
                    holes++;
                }
            }

            Assert.Equal(0, a.LeakedElements);
            Assert.True(holes <= a.Tail);
        }
    }

    [Fact]
    public void State_RoundTripsThroughCopyAndRestore_AndContinuesIdentically()
    {
        var rng = new Random(7);
        var a = new BlockArena(2000);
        var live = new List<(int First, int N)>();
        for (int step = 0; step < 300; step++)
        {
            if (live.Count > 0 && rng.Next(100) < 50)
            {
                int k = rng.Next(live.Count);
                a.Free(live[k].First, live[k].N);
                live.RemoveAt(k);
            }
            else
            {
                int n = 1 + rng.Next(30);
                int f = a.Alloc(n);
                if (f >= 0)
                {
                    live.Add((f, n));
                }
            }
        }

        var packed = new ulong[a.FreeBlockCount];
        a.CopyFree(packed);
        int words = a.LadderWordCount;

        var b = new BlockArena(2000) { Tail = a.Tail };
        a.LadderBits.AsSpan(0, words).CopyTo(b.LadderBits);
        Assert.True(b.RestoreFree(packed));
        Assert.Equal(a.FreeBlockCount, b.FreeBlockCount);

        var packedB = new ulong[b.FreeBlockCount];
        b.CopyFree(packedB);
        Assert.Equal(packed, packedB);

        // Дальнейшие выделения и освобождения идут одинаково.
        for (int step = 0; step < 200; step++)
        {
            int n = 1 + rng.Next(40);
            if (rng.Next(3) == 0 && live.Count > 0)
            {
                int k = rng.Next(live.Count);
                a.Free(live[k].First, live[k].N);
                b.Free(live[k].First, live[k].N);
                live.RemoveAt(k);
            }
            else
            {
                int fa = a.Alloc(n);
                int fb = b.Alloc(n);
                Assert.Equal(fa, fb);
                if (fa >= 0)
                {
                    live.Add((fa, n));
                }
            }
        }
    }

    [Fact]
    public void RestoreFree_RejectsOutOfRangeAndUnknownClass()
    {
        var a = new BlockArena(100) { Tail = 10 };
        Assert.False(a.RestoreFree(new[] { (ulong)BlockArena.ClassOf(8) << 32 | 5UL }));   // 5 + 8 > Tail
        a.ClearState();
        Assert.False(a.RestoreFree(new[] { 999UL << 32 }));
        a.ClearState();
        Assert.True(a.RestoreFree(new[] { (ulong)BlockArena.ClassOf(8) << 32 | 2UL }));
    }
}
