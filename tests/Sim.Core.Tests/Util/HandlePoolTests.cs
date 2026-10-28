using Sim.Core.Util;

namespace Sim.Core.Tests.Util;

public class HandlePoolTests
{
    [Fact]
    public void Allocate_ReturnsSequentialSlots_WithOddGeneration()
    {
        var pool = new HandlePool(4);
        for (int i = 0; i < 4; i++)
        {
            Assert.True(pool.TryAllocate(out int idx, out int gen));
            Assert.Equal(i, idx);
            Assert.Equal(1, gen);
        }

        Assert.Equal(4, pool.AliveCount);
        Assert.Equal(4, pool.HighWater);
    }

    [Fact]
    public void Allocate_WhenFull_ReturnsFalse()
    {
        var pool = new HandlePool(2);
        Assert.True(pool.TryAllocate(out _, out _));
        Assert.True(pool.TryAllocate(out _, out _));
        Assert.False(pool.TryAllocate(out int idx, out int gen));
        Assert.Equal(-1, idx);
        Assert.Equal(0, gen);
        Assert.Equal(2, pool.AliveCount);
    }

    [Fact]
    public void Free_MakesHandleInvalid_AndBumpsGenerationToEven()
    {
        var pool = new HandlePool(2);
        pool.TryAllocate(out int idx, out int gen);
        Assert.True(pool.IsAlive(idx, gen));
        pool.Free(idx);
        Assert.False(pool.IsAlive(idx));
        Assert.False(pool.IsAlive(idx, gen));
        Assert.Equal(2, pool.GenerationOf(idx));
        Assert.Equal(0, pool.AliveCount);
    }

    [Fact]
    public void Reuse_IsLifo_AndGenerationAdvances()
    {
        var pool = new HandlePool(8);
        for (int i = 0; i < 4; i++)
        {
            pool.TryAllocate(out _, out _);
        }

        pool.Free(1);
        pool.Free(3);

        pool.TryAllocate(out int a, out int genA);
        pool.TryAllocate(out int b, out int genB);
        Assert.Equal(3, a); // последним освобождён - первым выдан
        Assert.Equal(1, b);
        Assert.Equal(3, genA);
        Assert.Equal(3, genB);

        pool.TryAllocate(out int c, out _);
        Assert.Equal(4, c); // free-list пуст - новый слот
        Assert.Equal(5, pool.HighWater);
    }

    [Fact]
    public void StaleHandle_IsNotAlive_AfterReuse()
    {
        var pool = new HandlePool(1);
        pool.TryAllocate(out int idx, out int oldGen);
        pool.Free(idx);
        pool.TryAllocate(out int idx2, out int newGen);
        Assert.Equal(idx, idx2);
        Assert.NotEqual(oldGen, newGen);
        Assert.False(pool.IsAlive(idx, oldGen));
        Assert.True(pool.IsAlive(idx, newGen));
    }

    [Fact]
    public void InvalidIndices_AreNotAlive()
    {
        var pool = new HandlePool(4);
        pool.TryAllocate(out _, out _);
        Assert.False(pool.IsAlive(-1));
        Assert.False(pool.IsAlive(1));  // ещё не выдавался (>= HighWater)
        Assert.False(pool.IsAlive(100));
        Assert.False(pool.IsAlive(0, 0)); // Null-поколение
        Assert.False(pool.IsAlive(-1, 1));
    }

    [Fact]
    public void ChurnKeepsHighWaterBounded()
    {
        var pool = new HandlePool(16);
        for (int round = 0; round < 1000; round++)
        {
            pool.TryAllocate(out int idx, out _);
            pool.Free(idx);
        }

        Assert.Equal(1, pool.HighWater);
        Assert.Equal(0, pool.AliveCount);
    }

    [Fact]
    public void ZeroCapacity_NeverAllocates()
    {
        var pool = new HandlePool(0);
        Assert.False(pool.TryAllocate(out _, out _));
        Assert.Equal(0, pool.Capacity);
    }

    [Fact]
    public void AdoptDense_MarksPrefixAliveWithGenerationOne_AndPeekIndexPredictsNext()
    {
        var pool = new HandlePool(8);
        pool.AdoptDense(5);
        Assert.Equal(5, pool.HighWater);
        Assert.Equal(5, pool.AliveCount);
        Assert.Equal(0, pool.FreeCount);
        for (int i = 0; i < 5; i++)
        {
            Assert.True(pool.IsAlive(i, 1));
        }

        Assert.Equal(5, pool.PeekIndex());
        pool.Free(2);
        pool.Free(4);
        Assert.Equal(4, pool.PeekIndex());   // LIFO: сначала последний освобождённый
        Assert.True(pool.TryAllocate(out int idx, out int gen));
        Assert.Equal(4, idx);
        Assert.Equal(3, gen);
        Assert.Equal(2, pool.PeekIndex());
        for (int k = 0; k < 4; k++)   // слот 2 из free-list, затем 5, 6, 7
        {
            Assert.True(pool.TryAllocate(out _, out _));
        }

        Assert.Equal(8, pool.HighWater);
        Assert.Equal(-1, pool.PeekIndex());
        Assert.False(pool.TryAllocate(out _, out _));
    }
}
