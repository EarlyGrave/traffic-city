using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using Sim.Core;
using Sim.Core.Api;

namespace Sim.Scenarios.Tools;

/// <summary>Результат чтения/проверки файла журнала (ошибка - код, не исключение).</summary>
public enum ReplayStatus
{
    Ok = 0,
    BadMagic,
    UnsupportedVersion,
    Truncated,
    BadChecksum,
    BadBlock,
    MissingBlock,
    BadCommandOrder,
    BadCheckpoints,

    /// <summary>Версия мира или API ядра у файла не совпадает с текущей: журнал действителен только для той же версии поведения (save-format §10).</summary>
    VersionMismatch,
}

/// <summary>Контрольная точка: хэш состояния на тике <see cref="Tick"/> (после <c>Step</c>, до команд этого тика).</summary>
public readonly record struct ReplayCheckpoint(long Tick, ulong Hash);

/// <summary>
/// Журнал команд = реплей (player-tools.md §9, save-format.md §10). Хост пишет каждую принятую <c>Enqueue</c> команду
/// <c>(Tick на момент Enqueue, Command 64 Б)</c>; контрольные хэши - каждые <see cref="DefaultInterval"/> тиков от <see cref="StartTick"/>.
/// <code>
///  0 magic "TCRP\r\n\x1a\n" (8) | 8 u16 format_version = 1 | 10 u16 0 | 12 u32 block_count
/// 16 блоки: u32 fourcc, u32 длина, данные ... | в конце u64 xxh64 всех предыдущих байт
/// RPLH: u64 base_hash, i64 start_tick, i32 world_version, i32 api_version, u64 content_fingerprint, i32 interval, i32 cp_count, i32 name_len, i64 end_tick, name[utf8], cp_count x (i64 tick, u64 hash)
/// CMDL: i32 count, i64[count] Tick (неубывающие), raw[count][64]
/// SAVE (необязательный): сырой контейнер ядра на StartTick; SCEN (необязательный): текст JSON сценария-источника
/// </code>
/// </summary>
public sealed class ReplayLog
{
    public const int DefaultInterval = 1200;
    public const int FormatVersion = 1;
    public const int CommandSize = 64;

    private const uint IdRplh = 0x48_4C_50_52;   // "RPLH" в little-endian
    private const uint IdCmdl = 0x4C_44_4D_43;   // "CMDL"
    private const uint IdSave = 0x45_56_41_53;   // "SAVE"
    private const uint IdScen = 0x4E_45_43_53;   // "SCEN"
    private const int MaxName = 4096;

    private static ReadOnlySpan<byte> Magic => "TCRP\r\n\x1a\n"u8;

    /// <summary><c>state_hash</c> на <see cref="StartTick"/>.</summary>
    public ulong BaseHash { get; set; }

    public long StartTick { get; set; }

    /// <summary>Тик конца записи (последний <c>Step</c> рекордера); по нему <c>replay</c> без <c>--until</c> знает, докуда идти.</summary>
    public long EndTick { get; set; }

    public int WorldVersion { get; set; } = Simulation.CurrentWorldVersion;

    public int ApiVersion { get; set; } = CoreInfo.ApiVersion;

    /// <summary>Отпечаток контента (0 = не задан).</summary>
    public ulong ContentFingerprint { get; set; }

    /// <summary>Шаг контрольных точек в тиках (&gt;= 1).</summary>
    public int Interval { get; set; } = DefaultInterval;

    /// <summary>Имя сценария-источника (пусто, если неизвестно).</summary>
    public string Scenario { get; set; } = string.Empty;

    public List<ReplayCheckpoint> Checkpoints { get; } = [];

    public List<long> CommandTicks { get; } = [];

    public List<Command> Commands { get; } = [];

    /// <summary>Сырой контейнер сейва на <see cref="StartTick"/> (баг-репорт); null - нет.</summary>
    public byte[]? EmbeddedSave { get; set; }

    /// <summary>Текст JSON сценария-источника (баг-репорт); null - нет.</summary>
    public string? ScenarioJson { get; set; }

    /// <summary>Версии файла совпадают с текущими версиями ядра? Иначе реплей невозможен (понятная ошибка вместо ложной «порчи»).</summary>
    public ReplayStatus CheckCompatible(out string message)
    {
        if (WorldVersion != Simulation.CurrentWorldVersion || ApiVersion != CoreInfo.ApiVersion)
        {
            message = $"log was written by core world_version={WorldVersion} api_version={ApiVersion}, this build is world_version={Simulation.CurrentWorldVersion} api_version={CoreInfo.ApiVersion}";
            return ReplayStatus.VersionMismatch;
        }

        message = string.Empty;
        return ReplayStatus.Ok;
    }

    public byte[] ToBytes()
    {
        using var ms = new MemoryStream();
        ms.Write(Magic);
        Span<byte> hdr = stackalloc byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(hdr, FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(hdr[2..], 0);
        int blocks = 2 + (EmbeddedSave != null ? 1 : 0) + (ScenarioJson != null ? 1 : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[4..], (uint)blocks);
        ms.Write(hdr);
        WriteBlock(ms, IdRplh, BuildHeaderBlock());
        WriteBlock(ms, IdCmdl, BuildCommandBlock());
        if (EmbeddedSave != null)
        {
            WriteBlock(ms, IdSave, EmbeddedSave);
        }

        if (ScenarioJson != null)
        {
            WriteBlock(ms, IdScen, Encoding.UTF8.GetBytes(ScenarioJson));
        }

        byte[] body = ms.ToArray();
        byte[] file = new byte[body.Length + 8];
        body.CopyTo(file, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(body.Length), XxHash64.HashToUInt64(body));
        return file;
    }

    /// <summary>Разбирает файл. Любая порча - код статуса, исключений нет; <paramref name="log"/> валиден только при <see cref="ReplayStatus.Ok"/>.</summary>
    public static ReplayStatus TryParse(ReadOnlySpan<byte> file, out ReplayLog log)
    {
        log = new ReplayLog();
        if (file.Length < 8 || !file[..8].SequenceEqual(Magic))
        {
            return ReplayStatus.BadMagic;
        }

        if (file.Length < 16 + 8)
        {
            return ReplayStatus.Truncated;
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(file[8..]) != FormatVersion || BinaryPrimitives.ReadUInt16LittleEndian(file[10..]) != 0)
        {
            return ReplayStatus.UnsupportedVersion;
        }

        ReadOnlySpan<byte> body = file[..^8];
        if (XxHash64.HashToUInt64(body) != BinaryPrimitives.ReadUInt64LittleEndian(file[^8..]))
        {
            return ReplayStatus.BadChecksum;
        }

        uint blockCount = BinaryPrimitives.ReadUInt32LittleEndian(file[12..]);
        int pos = 16;
        bool haveHeader = false;
        bool haveCommands = false;
        for (uint i = 0; i < blockCount; i++)
        {
            if (body.Length - pos < 8)
            {
                return ReplayStatus.Truncated;
            }

            uint id = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]);
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(body[(pos + 4)..]);
            pos += 8;
            if (len > (uint)(body.Length - pos))
            {
                return ReplayStatus.Truncated;
            }

            ReadOnlySpan<byte> data = body.Slice(pos, (int)len);
            pos += (int)len;
            ReplayStatus st = ReplayStatus.Ok;
            switch (id)
            {
                case IdRplh when !haveHeader:
                    st = log.ParseHeaderBlock(data);
                    haveHeader = true;
                    break;
                case IdCmdl when !haveCommands:
                    st = log.ParseCommandBlock(data);
                    haveCommands = true;
                    break;
                case IdSave when log.EmbeddedSave == null:
                    log.EmbeddedSave = data.ToArray();
                    break;
                case IdScen when log.ScenarioJson == null:
                    try
                    {
                        log.ScenarioJson = new UTF8Encoding(false, true).GetString(data);
                    }
                    catch (ArgumentException)
                    {
                        st = ReplayStatus.BadBlock;
                    }

                    break;
                case IdRplh or IdCmdl or IdSave or IdScen:
                    st = ReplayStatus.BadBlock;   // дубликат
                    break;
            }

            if (st != ReplayStatus.Ok)
            {
                return st;
            }
        }

        if (pos != body.Length)
        {
            return ReplayStatus.BadBlock;
        }

        return haveHeader && haveCommands ? ReplayStatus.Ok : ReplayStatus.MissingBlock;
    }

    /// <summary>Читает файл с диска; ошибка ввода-вывода - <see cref="ReplayStatus.Truncated"/> и текст в <paramref name="message"/>.</summary>
    public static ReplayStatus TryLoad(string path, out ReplayLog log, out string message)
    {
        message = string.Empty;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log = new ReplayLog();
            message = e.Message;
            return ReplayStatus.Truncated;
        }

        return TryParse(bytes, out log);
    }

    private byte[] BuildHeaderBlock()
    {
        byte[] name = Encoding.UTF8.GetBytes(Scenario);
        byte[] b = new byte[52 + name.Length + (Checkpoints.Count * 16)];
        Span<byte> s = b;
        BinaryPrimitives.WriteUInt64LittleEndian(s, BaseHash);
        BinaryPrimitives.WriteInt64LittleEndian(s[8..], StartTick);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], WorldVersion);
        BinaryPrimitives.WriteInt32LittleEndian(s[20..], ApiVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(s[24..], ContentFingerprint);
        BinaryPrimitives.WriteInt32LittleEndian(s[32..], Interval);
        BinaryPrimitives.WriteInt32LittleEndian(s[36..], Checkpoints.Count);
        BinaryPrimitives.WriteInt32LittleEndian(s[40..], name.Length);
        BinaryPrimitives.WriteInt64LittleEndian(s[44..], EndTick);
        name.CopyTo(s[52..]);
        int p = 52 + name.Length;
        foreach (ReplayCheckpoint c in Checkpoints)
        {
            BinaryPrimitives.WriteInt64LittleEndian(s[p..], c.Tick);
            BinaryPrimitives.WriteUInt64LittleEndian(s[(p + 8)..], c.Hash);
            p += 16;
        }

        return b;
    }

    private ReplayStatus ParseHeaderBlock(ReadOnlySpan<byte> s)
    {
        if (s.Length < 52)
        {
            return ReplayStatus.Truncated;
        }

        BaseHash = BinaryPrimitives.ReadUInt64LittleEndian(s);
        StartTick = BinaryPrimitives.ReadInt64LittleEndian(s[8..]);
        WorldVersion = BinaryPrimitives.ReadInt32LittleEndian(s[16..]);
        ApiVersion = BinaryPrimitives.ReadInt32LittleEndian(s[20..]);
        ContentFingerprint = BinaryPrimitives.ReadUInt64LittleEndian(s[24..]);
        Interval = BinaryPrimitives.ReadInt32LittleEndian(s[32..]);
        int cps = BinaryPrimitives.ReadInt32LittleEndian(s[36..]);
        int nameLen = BinaryPrimitives.ReadInt32LittleEndian(s[40..]);
        EndTick = BinaryPrimitives.ReadInt64LittleEndian(s[44..]);
        if (StartTick < 0 || EndTick < StartTick || Interval < 1 || cps < 0 || nameLen < 0 || nameLen > MaxName || (long)s.Length != 52L + nameLen + (16L * cps))
        {
            return ReplayStatus.BadBlock;
        }

        try
        {
            Scenario = new UTF8Encoding(false, true).GetString(s.Slice(52, nameLen));
        }
        catch (ArgumentException)
        {
            return ReplayStatus.BadBlock;
        }

        int p = 52 + nameLen;
        long prev = StartTick;
        for (int i = 0; i < cps; i++)
        {
            long t = BinaryPrimitives.ReadInt64LittleEndian(s[p..]);
            if (t <= prev)
            {
                return ReplayStatus.BadCheckpoints;
            }

            prev = t;
            Checkpoints.Add(new ReplayCheckpoint(t, BinaryPrimitives.ReadUInt64LittleEndian(s[(p + 8)..])));
            p += 16;
        }

        return ReplayStatus.Ok;
    }

    private byte[] BuildCommandBlock()
    {
        int n = Commands.Count;
        byte[] b = new byte[4 + (n * 8) + (n * CommandSize)];
        Span<byte> s = b;
        BinaryPrimitives.WriteInt32LittleEndian(s, n);
        for (int i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(s[(4 + (i * 8))..], CommandTicks[i]);
        }

        int raw = 4 + (n * 8);
        for (int i = 0; i < n; i++)
        {
            Command c = Commands[i];
            MemoryMarshal.Write(s[(raw + (i * CommandSize))..], in c);
        }

        return b;
    }

    private ReplayStatus ParseCommandBlock(ReadOnlySpan<byte> s)
    {
        if (s.Length < 4)
        {
            return ReplayStatus.Truncated;
        }

        int n = BinaryPrimitives.ReadInt32LittleEndian(s);
        if (n < 0 || (long)s.Length != 4L + (n * (8L + CommandSize)))
        {
            return ReplayStatus.BadBlock;
        }

        int raw = 4 + (n * 8);
        long prev = 0;
        for (int i = 0; i < n; i++)
        {
            long t = BinaryPrimitives.ReadInt64LittleEndian(s[(4 + (i * 8))..]);
            if (t < prev)
            {
                return ReplayStatus.BadCommandOrder;
            }

            prev = t;
            CommandTicks.Add(t);
            Commands.Add(MemoryMarshal.Read<Command>(s[(raw + (i * CommandSize))..]));
        }

        return ReplayStatus.Ok;
    }

    private static void WriteBlock(MemoryStream ms, uint id, ReadOnlySpan<byte> data)
    {
        Span<byte> h = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(h, id);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)data.Length);
        ms.Write(h);
        ms.Write(data);
    }
}

/// <summary>
/// Запись журнала поверх <see cref="Simulation"/>: клиент ставит команды и шагает через рекордер. Принятая команда пишется с текущим тиком,
/// отброшенная (<c>Enqueue == false</c>) - нет. <see cref="Step"/> делится на границах контрольных точек. Превью не пишется (его здесь нет).
/// </summary>
public sealed class ReplayRecorder
{
    private readonly Simulation _sim;

    public ReplayRecorder(Simulation sim, int interval = ReplayLog.DefaultInterval, string scenario = "", ulong contentFingerprint = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, 1);
        _sim = sim;
        Log = new ReplayLog
        {
            BaseHash = sim.ComputeStateHash(),
            StartTick = sim.Tick,
            EndTick = sim.Tick,
            Interval = interval,
            Scenario = scenario,
            ContentFingerprint = contentFingerprint,
        };
    }

    /// <summary>Журнал; пополняется по ходу записи.</summary>
    public ReplayLog Log { get; }

    public bool Enqueue(in Command cmd)
    {
        if (!_sim.Enqueue(in cmd))
        {
            return false;
        }

        Log.CommandTicks.Add(_sim.Tick);
        Log.Commands.Add(cmd);
        return true;
    }

    /// <summary>Закрывает запись: хэш на текущем тике становится последней контрольной точкой (если на этом тике её ещё нет).</summary>
    public ReplayLog Finish()
    {
        Log.EndTick = _sim.Tick;
        if (_sim.Tick > Log.StartTick && (Log.Checkpoints.Count == 0 || Log.Checkpoints[^1].Tick < _sim.Tick))
        {
            Log.Checkpoints.Add(new ReplayCheckpoint(_sim.Tick, _sim.ComputeStateHash()));
        }

        return Log;
    }

    public void Step(int ticks)
    {
        int left = ticks;
        while (left > 0)
        {
            long next = Log.StartTick + ((((_sim.Tick - Log.StartTick) / Log.Interval) + 1) * Log.Interval);
            int n = (int)Math.Min(left, next - _sim.Tick);
            _sim.Step(n);
            left -= n;
            if (_sim.Tick == next)
            {
                Log.Checkpoints.Add(new ReplayCheckpoint(next, _sim.ComputeStateHash()));
            }

            Log.EndTick = _sim.Tick;
        }
    }
}
