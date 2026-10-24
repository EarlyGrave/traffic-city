using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Core.Routing;

/// <summary>
/// Кэш магистралей по паре зон (routing.md 4.1, 4.3). Хранит индекс записи <see cref="RoutePool"/>
/// (RefCount = 0, FromZone/ToZone = пара). Шаги магистрали - DirSeg в поле <c>RouteStep.Lane</c>.
/// Инвалидация ленивая по <c>NetworkVersion</c>, вытеснение - минимальный <c>LastUsedTick</c> в окне из 8 слотов пула.
/// </summary>
internal sealed class RouteCache
{
    public const int EvictWindow = 8;

    private readonly RoutePool _pool;
    private readonly FixedHashMap _map;
    private readonly int _maxZones;

    public RouteCache(RoutePool pool, int maxZones)
    {
        _pool = pool;
        _maxZones = maxZones < 1 ? 1 : maxZones;
        _map = new FixedHashMap(pool.Pool.Capacity < 1 ? 1 : pool.Pool.Capacity);
    }

    public int Count => _map.Count;

    public long Key(int from, int to) => ((long)from * _maxZones) + to;

    /// <summary>
    /// Индекс магистрали в пуле или -1. Запись устаревшей версии сети удаляется и освобождается.
    /// При попадании обновляет <c>LastUsedTick</c>.
    /// </summary>
    public int Lookup(int from, int to, int networkVersion, long tick, int weightsVersion = -1)
    {
        long key = Key(from, to);
        if (!_map.TryGet(key, out int idx))
        {
            return -1;
        }

        if (_pool.NetworkVersion[idx] != networkVersion || (weightsVersion >= 0 && _pool.WeightsVersion[idx] != weightsVersion))
        {
            _map.Remove(key);
            _pool.Free(new RouteHandle(idx, _pool.Pool.GenerationOf(idx)));
            return -1;
        }

        _pool.LastUsedTick[idx] = tick;
        return idx;
    }

    /// <summary>Регистрирует магистраль (запись уже лежит в пуле с FromZone/ToZone пары).</summary>
    public bool Insert(int from, int to, RouteHandle h, long tick)
    {
        if (!_map.TrySet(Key(from, to), h.Index))
        {
            return false;
        }

        _pool.LastUsedTick[h.Index] = tick;
        return true;
    }

    /// <summary>
    /// Вытесняет одну магистраль: среди <see cref="EvictWindow"/> слотов пула от хэша <paramref name="seed"/> выбирается
    /// живая запись с RefCount == 0 и FromZone &gt;= 0 с минимальным LastUsedTick (ничья - меньший индекс).
    /// false = в окне магистралей нет.
    /// </summary>
    public bool EvictOne(long seed)
    {
        int cap = _pool.Pool.Capacity;
        if (cap == 0)
        {
            return false;
        }

        int start = (int)(((ulong)seed * 2654435761UL) % (ulong)cap);
        int best = -1;
        for (int k = 0; k < EvictWindow && k < cap; k++)
        {
            int idx = (start + k) % cap;
            if (!_pool.Pool.IsAlive(idx) || _pool.RefCount[idx] != 0 || _pool.FromZone[idx] < 0)
            {
                continue;
            }

            if (best < 0 || _pool.LastUsedTick[idx] < _pool.LastUsedTick[best] ||
                (_pool.LastUsedTick[idx] == _pool.LastUsedTick[best] && idx < best))
            {
                best = idx;
            }
        }

        if (best < 0)
        {
            return false;
        }

        _map.Remove(Key(_pool.FromZone[best], _pool.ToZone[best]));
        _pool.Free(new RouteHandle(best, _pool.Pool.GenerationOf(best)));
        return true;
    }
}
