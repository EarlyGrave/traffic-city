using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Sim.Core.Api;

namespace Sim.Host;

/// <summary>
/// Поток симуляции вне главного потока клиента (ADR-0024, scale.md S9). Без Godot. Владеет <see cref="Simulation"/>: единственный, кто вызывает его методы
/// (кроме <see cref="AcquireExclusive"/>). Главный поток: публикует <see cref="SetTargetTick"/>, подаёт команды (<see cref="TryEnqueue"/>), вид
/// (<see cref="SetRenderView"/>, последний выигрывает), запросы (<see cref="TryRequestPickVehicle"/>), забирает кадры (<see cref="TryAcquireFrame"/>) и ответы.
/// <para>
/// Цикл потока: применить вход (команды -> <c>Enqueue</c> ядра + журнал по тику применения, вид, запросы) -> пока <c>Tick &lt; TargetTick</c>:
/// <c>Step(min(Δ, MaxStepBatch))</c> и публикация кадра; иначе спать на событии. Команда, поданная во время тика T+1, применяется в тике T+2 (+1 тик лага).
/// </para>
/// <para>Главный поток - единственный писатель в кольца/вид/цель; вызывать публичные методы с двух потоков одновременно нельзя.</para>
/// </summary>
public sealed class SimThreadHost : IDisposable
{
    private const int RoundTo = 1024;

    private readonly Simulation _sim;
    private readonly SimHostOptions _opt;
    private readonly Thread _thread;

    private readonly SpscRing<Command> _commands;
    private readonly SpscRing<HostRequest> _requests;
    private readonly SpscRing<HostResponse> _responses;

    private readonly ManualResetEventSlim _wake = new(false);
    private readonly ManualResetEventSlim _paused = new(false);
    private readonly ManualResetEventSlim _resume = new(false);
    private readonly ManualResetEventSlim _exited = new(false);

    private readonly SimFrame[] _frames = { new(), new(), new() };
    private int _writeIdx;         // поток симуляции
    private int _readIdx = 1;           // главный поток
    private int _midIdx = 2;            // общий слот; бит 4 - «свежий кадр»
    private const int NewFlag = 4;

    private readonly List<JournalEntry> _journal;
    private readonly object _journalLock = new();

    // вид: seqlock, один писатель (главный поток)
    private int _viewSeq;
    private float _vx0, _vz0, _vx1, _vz1;
    private bool _vAll;
    private int _viewApplied;

    private long _target;
    private int _stop;
    private int _exclusiveRequested;
    private int _republish;
    private int _state = (int)HostState.Running;
    private Exception? _fault;
    private int _disposed;

    private long _nextRequestId;
    private long _published;
    private long _acquired;
    private long _commandsApplied;
    private long _commandsRejected;
    private long _commandsRingFull;
    private long _tick;
    private long _simAllocated;
    private int _simThreadId;

    /// <summary>Берёт владение <paramref name="simulation"/> (освободит в <see cref="Dispose"/>), публикует кадр нулевого тика и запускает поток.</summary>
    public SimThreadHost(Simulation simulation, SimHostOptions? options = null)
    {
        _sim = simulation ?? throw new ArgumentNullException(nameof(simulation));
        _opt = options ?? new SimHostOptions();
        _commands = new SpscRing<Command>(_opt.CommandRingCapacity);
        _requests = new SpscRing<HostRequest>(_opt.RequestRingCapacity);
        _responses = new SpscRing<HostResponse>(_opt.ResponseRingCapacity);
        _journal = new List<JournalEntry>(Math.Max(16, _opt.JournalCapacity));
        _tick = _sim.Tick;
        _target = _tick;
        _viewApplied = 0;
        int cap = Math.Max(RoundTo, RoundUp(_opt.InitialFrameCapacity));
        foreach (SimFrame f in _frames)
        {
            Grow(f, cap, cap);
        }

        PublishFrame(0, 0);
        _thread = new Thread(Run) { IsBackground = true, Name = _opt.ThreadName };
        _thread.Start();
    }

    // ---------------------------------------------------------------- главный поток

    public HostState State => (HostState)Volatile.Read(ref _state);

    /// <summary>Исключение, остановившее поток симуляции (<see cref="HostState.Faulted"/>).</summary>
    public Exception? Fault => Volatile.Read(ref _fault);

    /// <summary>Последний тик ядра, завершённый потоком симуляции.</summary>
    public long Tick => Volatile.Read(ref _tick);

    public long TargetTick => Volatile.Read(ref _target);

    /// <summary>Сколько тиков симуляция должна догнать (HUD «отставание»); не отрицательно.</summary>
    public long TickLag => Math.Max(0, TargetTick - Tick);

    public long FramesPublished => Volatile.Read(ref _published);

    /// <summary>Сколько кадров забрал главный поток; разница с опубликованными - перезаписанные («последний выигрывает»).</summary>
    public long FramesAcquired => Volatile.Read(ref _acquired);

    public long CommandsApplied => Volatile.Read(ref _commandsApplied);

    /// <summary>Команды, отброшенные очередью ядра (<c>Enqueue</c> вернул false).</summary>
    public long CommandsRejectedByCore => Volatile.Read(ref _commandsRejected);

    /// <summary>Команды, не влезшие в кольцо (<see cref="TryEnqueue"/> вернул false).</summary>
    public long CommandsRingFull => Volatile.Read(ref _commandsRingFull);

    /// <summary>Байты, аллоцированные потоком симуляции к последней публикации.</summary>
    public long SimThreadAllocatedBytes => Volatile.Read(ref _simAllocated);

    /// <summary>Задаёт, до какого тика шагать (аккумулятор × скорость игры). Пауза - не увеличивать. Меньше текущего тика - просто ждать.</summary>
    public void SetTargetTick(long target)
    {
        Volatile.Write(ref _target, target);
        _wake.Set();
    }

    /// <summary>Команда в кольцо; false - кольцо полно (команда не принята) либо хост не работает.</summary>
    public bool TryEnqueue(in Command cmd)
    {
        if (State != HostState.Running || !_commands.TryWrite(in cmd))
        {
            _commandsRingFull++;
            return false;
        }

        _wake.Set();
        return true;
    }

    /// <summary>Вид снапшота: «последний выигрывает», применяется между <c>Step</c>. Нечисловые координаты - вид «всё» (как у ядра).</summary>
    public void SetRenderView(float x0, float z0, float x1, float z1)
    {
        WriteView(false, x0, z0, x1, z1);
    }

    public void SetRenderViewAll() => WriteView(true, 0, 0, 0, 0);

    private void WriteView(bool all, float x0, float z0, float x1, float z1)
    {
        Interlocked.Increment(ref _viewSeq); // нечётное: идёт запись
        _vAll = all;
        _vx0 = x0;
        _vz0 = z0;
        _vx1 = x1;
        _vz1 = z1;
        Interlocked.Increment(ref _viewSeq); // чётное: запись закончена
        _wake.Set();
    }

    /// <summary>Запрос «машина у точки»; возвращает id (&gt; 0) для сопоставления с ответом, 0 - кольцо запросов полно.</summary>
    public long TryRequestPickVehicle(float x, float z, float radius) => Request(HostRequestKind.PickVehicle, x, z, radius);

    public long TryRequestPickPedestrian(float x, float z, float radius) => Request(HostRequestKind.PickPedestrian, x, z, radius);

    private long Request(HostRequestKind kind, float x, float z, float radius)
    {
        long id = _nextRequestId + 1;
        var r = new HostRequest { Id = id, Kind = kind, X = x, Z = z, Radius = radius };
        if (State != HostState.Running || !_requests.TryWrite(in r))
        {
            return 0;
        }

        _nextRequestId = id;
        _wake.Set();
        return id;
    }

    /// <summary>Следующий ответ на запрос (в порядке запросов); false - пока нет.</summary>
    public bool TryReadResponse(out HostResponse response) => _responses.TryRead(out response);

    /// <summary>
    /// Забирает самый свежий кадр. true и новый кадр - если с прошлого вызова что-то опубликовано; иначе false, а <paramref name="frame"/> - прежний
    /// кадр (владелец - главный поток, пока не вызван следующий <see cref="TryAcquireFrame"/>).
    /// </summary>
    public bool TryAcquireFrame(out SimFrame frame)
    {
        if ((Volatile.Read(ref _midIdx) & NewFlag) != 0)
        {
            int old = Interlocked.Exchange(ref _midIdx, _readIdx);
            _readIdx = old & 3;
            Interlocked.Increment(ref _acquired);
            frame = _frames[_readIdx];
            return true;
        }

        frame = _frames[_readIdx];
        return false;
    }

    /// <summary>
    /// Просит паузу: поток симуляции дошагивает текущий <c>Step</c> и ждёт; пока не освобождён результат, главный читает/пишет ядро напрямую
    /// (сейв, загрузка, <c>Network</c> после <c>BuildRoad</c>, <c>ComputeStateHash</c>). После освобождения публикуется свежий кадр.
    /// Если поток уже остановлен (Faulted/Stopped), возвращает доступ сразу. Не вкладывать.
    /// </summary>
    public ExclusiveAccess AcquireExclusive()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        _paused.Reset();
        _resume.Reset();
        Volatile.Write(ref _exclusiveRequested, 1);
        _wake.Set();
        while (!_paused.Wait(5))
        {
            if (_exited.IsSet)
            {
                break;
            }
        }

        return new ExclusiveAccess(this, _sim);
    }

    internal void ReleaseExclusive()
    {
        Volatile.Write(ref _republish, 1);
        Volatile.Write(ref _exclusiveRequested, 0);
        _resume.Set();
        _wake.Set();
    }

    /// <summary>Копия журнала команд (тик применения, команда) для реплея; выделяет память - вызывать редко.</summary>
    public JournalEntry[] CopyJournal()
    {
        lock (_journalLock)
        {
            return _journal.ToArray();
        }
    }

    /// <summary>Останавливает поток, ждёт его и освобождает <see cref="Simulation"/>. Идемпотентно.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _stop, 1);
        _wake.Set();
        _resume.Set();
        if (Thread.CurrentThread != _thread)
        {
            _thread.Join();
        }

        _sim.Dispose();
        if (_state != (int)HostState.Faulted)
        {
            Volatile.Write(ref _state, (int)HostState.Stopped);
        }
    }

    // ---------------------------------------------------------------- поток симуляции

    private void Run()
    {
        _simThreadId = Environment.CurrentManagedThreadId;
        try
        {
            while (Volatile.Read(ref _stop) == 0)
            {
                _wake.Reset();
                if (Volatile.Read(ref _exclusiveRequested) == 1)
                {
                    _paused.Set();
                    _resume.Wait();
                    _resume.Reset();
                    continue;
                }

                DrainInput();
                if (Interlocked.Exchange(ref _republish, 0) == 1)
                {
                    PublishFrame(0, 0);
                }

                long target = Volatile.Read(ref _target);
                long tick = _sim.Tick;
                if (tick < target)
                {
                    int n = (int)Math.Min(target - tick, _opt.MaxStepBatch);
                    long t0 = Stopwatch.GetTimestamp();
                    _sim.Step(n);
                    long micros = (Stopwatch.GetTimestamp() - t0) * 1_000_000L / Stopwatch.Frequency;
                    Volatile.Write(ref _tick, _sim.Tick);
                    PublishFrame(micros, n);
                    continue;
                }

                _wake.Wait();
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _fault, ex);
            Volatile.Write(ref _state, (int)HostState.Faulted);
        }
        finally
        {
            _exited.Set();
            _paused.Set(); // не оставить главный поток в ожидании паузы
        }
    }

    private void DrainInput()
    {
        Debug.Assert(Environment.CurrentManagedThreadId == _simThreadId, "DrainInput вне потока симуляции");

        // вид: последний выигрывает
        int s1 = Volatile.Read(ref _viewSeq);
        if ((s1 & 1) == 0 && s1 != _viewApplied)
        {
            bool all = _vAll;
            float x0 = _vx0, z0 = _vz0, x1 = _vx1, z1 = _vz1;
            Thread.MemoryBarrier();
            if (Volatile.Read(ref _viewSeq) == s1)
            {
                if (all)
                {
                    _sim.SetRenderViewAll();
                }
                else
                {
                    _sim.SetRenderView(x0, z0, x1, z1);
                }

                _viewApplied = s1;
            }
        }

        long tick = _sim.Tick;
        while (_commands.TryRead(out Command cmd))
        {
            if (_sim.Enqueue(in cmd))
            {
                _commandsApplied++;
                lock (_journalLock)
                {
                    _journal.Add(new JournalEntry(tick, in cmd));
                }
            }
            else
            {
                _commandsRejected++;
            }
        }

        while (_requests.TryRead(out HostRequest req))
        {
            var resp = new HostResponse { RequestId = req.Id, Kind = req.Kind, Tick = tick };
            if (req.Kind == HostRequestKind.PickVehicle && _sim.TryPickVehicle(req.X, req.Z, req.Radius, out VehicleHandle vh))
            {
                resp.Found = true;
                resp.Index = vh.Index;
                resp.Generation = vh.Generation;
            }
            else if (req.Kind == HostRequestKind.PickPedestrian && _sim.TryPickPedestrian(req.X, req.Z, req.Radius, out PedHandle ph))
            {
                resp.Found = true;
                resp.Index = ph.Index;
                resp.Generation = ph.Generation;
            }

            if (!_responses.TryWrite(in resp))
            {
                break; // кольцо ответов полно: клиент не читает; запрос потерян
            }
        }
    }

    private void PublishFrame(long stepMicros, int stepTicks)
    {
        Debug.Assert(Environment.CurrentManagedThreadId == _simThreadId || _thread == null, "PublishFrame вне потока симуляции");
        SimFrame f = _frames[_writeIdx];
        int nv;
        while (!_sim.CopyVisibleVehicles(f.CurArr, f.PrevArr, out nv))
        {
            Grow(f, RoundUp(nv), f.PedCurArr.Length);
        }

        f.VehicleCount = nv;
        int np;
        while (!_sim.CopyVisiblePeds(f.PedCurArr, f.PedPrevArr, out np))
        {
            Grow(f, f.CurArr.Length, RoundUp(np));
        }

        f.PedCount = np;
        _sim.GetStats(out SimStats stats);
        f.Stats = stats;
        f.SnapshotTick = _sim.SnapshotTick;
        f.ViewVersion = _sim.ViewVersion;
        f.ActiveVehicleCount = _sim.ActiveVehicleCount;
        f.LastStepMicros = stepMicros;
        f.LastStepTicks = stepTicks;
        long alloc = GC.GetAllocatedBytesForCurrentThread();
        f.SimThreadAllocatedBytes = alloc;
        Volatile.Write(ref _simAllocated, alloc);
        f.Sequence = Interlocked.Increment(ref _published);

        int old = Interlocked.Exchange(ref _midIdx, _writeIdx | NewFlag);
        _writeIdx = old & 3;
    }

    private static int RoundUp(int n) => ((n + RoundTo - 1) / RoundTo) * RoundTo;

    private static void Grow(SimFrame f, int vehicles, int peds)
    {
        if (f.CurArr.Length < vehicles)
        {
            f.CurArr = new VehicleRenderData[vehicles];
            f.PrevArr = new VehicleRenderData[vehicles];
        }

        if (f.PedCurArr.Length < peds)
        {
            f.PedCurArr = new PedestrianRenderData[peds];
            f.PedPrevArr = new PedestrianRenderData[peds];
        }
    }
}
