using Sim.Core.Agents;
using Sim.Core.Routing;
using Sim.Core.Util;

namespace Sim.Core.Tests.Routing;

public class PendingTripRoutingTests
{
    private static PendingTrip Trip(int to, int routeGen = 0) => new() { ToZone = to, DestLane = -1, Route = -1, RouteGen = routeGen };

    [Fact]
    public void DefaultTrip_HasNoRoute()
    {
        Assert.False(default(PendingTrip).HasRoute);
        Assert.True(Trip(1, routeGen: 3).HasRoute);
    }

    [Fact]
    public void Unrouted_TracksEnqueueDequeueSetAndRemove()
    {
        var q = new PendingTrips(8);
        for (int i = 0; i < 5; i++)
        {
            Assert.True(q.TryEnqueue(Trip(i)));
        }

        Assert.Equal(5, q.Unrouted);

        PendingTrip t = q.At(2);
        t.Route = 7;
        t.RouteGen = 1;
        q.Set(2, in t);
        Assert.Equal(4, q.Unrouted);

        q.RemoveAt(0); // неразмаршрутизированная голова
        Assert.Equal(3, q.Unrouted);
        Assert.Equal(4, q.Count);
        Assert.Equal(1, q.At(0).ToZone);
        Assert.Equal(2, q.At(1).ToZone);
        Assert.True(q.At(1).HasRoute);
        Assert.Equal(4, q.At(3).ToZone);

        q.RemoveAt(1); // с маршрутом: счётчик не меняется
        Assert.Equal(3, q.Unrouted);

        Assert.True(q.TryDequeue(out PendingTrip head));
        Assert.Equal(1, head.ToZone);
        Assert.Equal(2, q.Unrouted);
        q.Clear();
        Assert.Equal(0, q.Unrouted);
    }

    [Fact]
    public void RingBuffer_RemoveAt_WorksAcrossWrap()
    {
        var rb = new RingBuffer<int>(4);
        rb.TryEnqueue(1);
        rb.TryEnqueue(2);
        rb.TryEnqueue(3);
        rb.TryDequeue(out _);
        rb.TryDequeue(out _);
        rb.TryEnqueue(4);
        rb.TryEnqueue(5);
        rb.TryEnqueue(6); // [3,4,5,6] c переносом через границу массива
        rb.RemoveAt(1);
        Assert.Equal(3, rb.Count);
        Assert.Equal(3, rb.At(0));
        Assert.Equal(5, rb.At(1));
        Assert.Equal(6, rb.At(2));
        rb.Set(1, 50);
        Assert.Equal(50, rb.At(1));
        Assert.True(rb.TryEnqueue(7));
        Assert.Equal(7, rb.At(3));
    }
}
