using Sim.Core.Agents;
using Sim.Core.Api;

namespace Sim.Core.Tests.Agents;

public class VehicleStoreTests
{
    [Fact]
    public void Spawn_ReturnsSequentialSlots_WithOddGeneration_AndDefaults()
    {
        var store = new VehicleStore(4);
        for (int i = 0; i < 4; i++)
        {
            Assert.True(store.TrySpawn(out VehicleHandle h));
            Assert.Equal(i, h.Index);
            Assert.Equal(1, h.Generation);
            Assert.True(store.IsAlive(h));
            Assert.Equal(-1, store.Lane[i]);
            Assert.Equal(-1, store.Ahead[i]);
            Assert.Equal(-1, store.Behind[i]);
            Assert.Equal(-1, store.GhostLane[i]);
            Assert.Equal(-1, store.Route[i]);
            Assert.Equal(-1, store.DestZone[i]);
            Assert.Equal(-1, store.BusRoute[i]);
        }

        Assert.Equal(4, store.AliveCount);
        Assert.Equal(4, store.Capacity);
    }

    [Fact]
    public void Spawn_WhenFull_Fails_AndReturnsNullHandle()
    {
        var store = new VehicleStore(2);
        Assert.True(store.TrySpawn(out _));
        Assert.True(store.TrySpawn(out _));
        Assert.False(store.TrySpawn(out VehicleHandle h));
        Assert.True(h.IsNull);
        Assert.Equal(2, store.AliveCount);
    }

    [Fact]
    public void Despawn_InvalidatesHandle_BumpsGeneration_AndIsIdempotent()
    {
        var store = new VehicleStore(2);
        store.TrySpawn(out VehicleHandle h);
        Assert.True(store.Despawn(h));
        Assert.False(store.IsAlive(h));
        Assert.Equal(2, store.Pool.GenerationOf(h.Index));
        Assert.False(store.Despawn(h));
        Assert.False(store.Despawn(VehicleHandle.Null));
        Assert.Equal(0, store.AliveCount);
    }

    [Fact]
    public void Reuse_Slot_GetsNewGeneration_OldHandleStaysInvalid_AndFieldsAreReset()
    {
        var store = new VehicleStore(1);
        store.TrySpawn(out VehicleHandle first);
        store.S[0] = 123f;
        store.Speed[0] = 9f;
        store.Route[0] = 5;
        store.Flags[0] = (byte)VehicleFlags.Stuck;
        store.Passengers[0] = 7;
        store.Despawn(first);

        Assert.True(store.TrySpawn(out VehicleHandle second));
        Assert.Equal(first.Index, second.Index);
        Assert.Equal(3, second.Generation);
        Assert.False(store.IsAlive(first));
        Assert.True(store.IsAlive(second));
        Assert.Equal(0f, store.S[0]);
        Assert.Equal(0f, store.Speed[0]);
        Assert.Equal(-1, store.Route[0]);
        Assert.Equal(0, store.Flags[0]);
        Assert.Equal(0, store.Passengers[0]);
        Assert.Equal(second, store.HandleOf(0));
    }

    [Fact]
    public void Reuse_IsLifo_AndHighWaterDoesNotGrow()
    {
        var store = new VehicleStore(8);
        var hs = new VehicleHandle[3];
        for (int i = 0; i < 3; i++)
        {
            store.TrySpawn(out hs[i]);
        }

        store.Despawn(hs[0]);
        store.Despawn(hs[2]);
        store.TrySpawn(out VehicleHandle a);
        store.TrySpawn(out VehicleHandle b);
        Assert.Equal(2, a.Index);
        Assert.Equal(0, b.Index);
        Assert.Equal(3, store.HighWater);
    }

    [Fact]
    public void ZeroCapacity_Store_IsValid()
    {
        var store = new VehicleStore(0);
        Assert.False(store.TrySpawn(out _));
    }
}
