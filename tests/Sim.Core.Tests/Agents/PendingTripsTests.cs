using Sim.Core.Agents;

namespace Sim.Core.Tests.Agents;

public class PendingTripsTests
{
    private static PendingTrip Trip(int n) => new() { FromZone = n, ToZone = n + 1, FromLane = n + 2, FromS = n * 0.5f, Type = (byte)(n & 7) };

    [Fact]
    public void Fifo_PreservesOrderAndFields()
    {
        var q = new PendingTrips(4);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(q.TryEnqueue(Trip(i)));
        }

        for (int i = 0; i < 3; i++)
        {
            Assert.True(q.TryDequeue(out PendingTrip t));
            Assert.Equal(Trip(i), t);
        }

        Assert.True(q.IsEmpty);
        Assert.False(q.TryDequeue(out _));
    }

    [Fact]
    public void Overflow_RejectsNewTrip_AndKeepsQueue()
    {
        var q = new PendingTrips(2);
        Assert.True(q.TryEnqueue(Trip(1)));
        Assert.True(q.TryEnqueue(Trip(2)));
        Assert.True(q.IsFull);
        Assert.False(q.TryEnqueue(Trip(3)));
        Assert.Equal(2, q.Count);
        q.TryDequeue(out PendingTrip t);
        Assert.Equal(Trip(1), t);
    }

    [Fact]
    public void Peek_DoesNotRemove_SoDeferredTripStaysAtFront()
    {
        var q = new PendingTrips(3);
        q.TryEnqueue(Trip(1));
        q.TryEnqueue(Trip(2));
        Assert.True(q.TryPeek(out PendingTrip a));
        Assert.True(q.TryPeek(out PendingTrip b));
        Assert.Equal(a, b);
        Assert.Equal(2, q.Count);
    }

    [Fact]
    public void WrapAround_ManyCycles_KeepsFifo()
    {
        var q = new PendingTrips(5);
        int nextIn = 0;
        int nextOut = 0;
        for (int round = 0; round < 50; round++)
        {
            for (int k = 0; k < 3; k++)
            {
                Assert.True(q.TryEnqueue(Trip(nextIn++)));
            }

            for (int k = 0; k < 3; k++)
            {
                Assert.True(q.TryDequeue(out PendingTrip t));
                Assert.Equal(Trip(nextOut++), t);
            }
        }

        Assert.True(q.IsEmpty);
        q.TryEnqueue(Trip(1));
        q.TryEnqueue(Trip(2));
        Assert.Equal(Trip(2), q.At(1));
    }
}
