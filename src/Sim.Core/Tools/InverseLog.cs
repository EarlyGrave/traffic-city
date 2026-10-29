using Sim.Core.Api;

namespace Sim.Core.Tools;

/// <summary>
/// Буфер обратных записей за последний <c>Step</c> (player-tools.md §8.1) и scratch-таблица перенумерации «старый handle -> новый» фазы 1 (§8.2).
/// Ни то ни другое не состояние мира: в хэш и сейв не входят, очищаются (записи - в начале <c>Step</c>, таблица - в конце фазы 1).
/// Все массивы выделяются один раз при создании мира.
/// </summary>
internal sealed class InverseLog
{
    public const int DefaultCommandCapacity = 4096;
    public const int MaxPerRecord = 64;

    public readonly Command[] Cmds;
    public readonly InverseRecord[] Recs;
    public readonly RemapEntry[] Remap;
    public int CmdCount;
    public int RecCount;
    public int RemapCount;

    /// <summary>Сегмент, построенный последней <c>BuildRoad</c> (-1 - нет): нужен обратной записи, которая знает индекс только после мутации.</summary>
    public int BuiltSeg = -1;

    /// <summary>Узел, созданный последней <c>SplitRoad</c> (-1 - нет), и точка разреза для обратной к <c>MergeAtNode</c> (T5).</summary>
    public int BuiltNode = -1;

    public float BuiltS;

    /// <summary>Половины, слитые последней <c>MergeAtNode</c> (до слияния): обратная <c>SplitRoad</c> восстанавливает их handle'ы (T5).</summary>
    public EntityRef MergedA, MergedB;

    public InverseLog(int commandCapacity, int recordCapacity)
    {
        Cmds = new Command[commandCapacity > 0 ? commandCapacity : DefaultCommandCapacity];
        Recs = new InverseRecord[recordCapacity > 0 ? recordCapacity : 1];
        Remap = new RemapEntry[(recordCapacity > 0 ? recordCapacity : 1) * 3];
    }

    public void ClearRecords()
    {
        CmdCount = 0;
        RecCount = 0;
    }

    public void ClearRemap() => RemapCount = 0;

    public void AddRemap(EntityKind kind, in EntityRef old, in EntityRef now)
    {
        if (RemapCount < Remap.Length)
        {
            Remap[RemapCount++] = new RemapEntry { Kind = kind, OldIndex = old.Index, OldGeneration = old.Generation, NewIndex = now.Index, NewGeneration = now.Generation };
        }
    }
}
