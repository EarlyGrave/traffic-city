using Sim.Core.Util;

namespace Sim.Core.Tests.Util;

public class RingBufferTests
{
    [Fact]
    public void Fifo_Order()
    {
        var rb = new RingBuffer<int>(4);
        for (int i = 1; i <= 3; i++)
        {
            Assert.True(rb.TryEnqueue(i));
        }

        Assert.Equal(3, rb.Count);
        for (int i = 1; i <= 3; i++)
        {
            Assert.True(rb.TryDequeue(out int v));
            Assert.Equal(i, v);
        }

        Assert.True(rb.IsEmpty);
        Assert.False(rb.TryDequeue(out _));
    }

    [Fact]
    public void Overflow_RejectsNewItem_AndKeepsOldOnes()
    {
        var rb = new RingBuffer<int>(2);
        Assert.True(rb.TryEnqueue(10));
        Assert.True(rb.TryEnqueue(20));
        Assert.True(rb.IsFull);
        Assert.False(rb.TryEnqueue(30));
        Assert.Equal(2, rb.Count);
        rb.TryDequeue(out int a);
        rb.TryDequeue(out int b);
        Assert.Equal(10, a);
        Assert.Equal(20, b);
    }

    [Fact]
    public void WrapAround_PreservesOrder()
    {
        var rb = new RingBuffer<long>(3);
        long next = 0;
        long expect = 0;
        for (int round = 0; round < 50; round++)
        {
            rb.TryEnqueue(next++);
            rb.TryEnqueue(next++);
            Assert.True(rb.TryDequeue(out long v));
            Assert.Equal(expect++, v);
            Assert.True(rb.TryDequeue(out v));
            Assert.Equal(expect++, v);
        }

        Assert.True(rb.IsEmpty);
    }

    [Fact]
    public void At_And_Peek_ReadWithoutRemoving_AcrossWrap()
    {
        var rb = new RingBuffer<int>(3);
        rb.TryEnqueue(1);
        rb.TryEnqueue(2);
        rb.TryEnqueue(3);
        rb.TryDequeue(out _);
        rb.TryEnqueue(4); // физически обёрнут
        Assert.Equal(2, rb.At(0));
        Assert.Equal(3, rb.At(1));
        Assert.Equal(4, rb.At(2));
        Assert.True(rb.TryPeek(out int p));
        Assert.Equal(2, p);
        Assert.Equal(3, rb.Count);
    }

    [Fact]
    public void Clear_EmptiesBuffer()
    {
        var rb = new RingBuffer<int>(2);
        rb.TryEnqueue(1);
        rb.Clear();
        Assert.True(rb.IsEmpty);
        Assert.False(rb.TryPeek(out _));
        Assert.True(rb.TryEnqueue(5));
    }
}
