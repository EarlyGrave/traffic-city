#pragma warning disable CA1861 // тест: литералы-массивы допустимы
using System;
using Sim.Core.Api;
using Sim.Core.Save;

namespace Sim.Core.Tests.Save;

/// <summary>T10: каркас миграций над таблицами на фиктивной цепочке v1 -> v2 -> v3 (боевых шагов пока нет, save-format.md §4).</summary>
public sealed class MigrationTests
{
    private static readonly uint _blk = SaveFormat.FourCc("TEST"u8);

    // v1: столбец 1 = скорость в км/ч (i32). v2 (смена единиц): столбец 2 = м/с (f32) считается из столбца 1, столбец 1 выбывает.
    // v3 (новое поле): столбец 3 = дефолт 7.
    private static readonly Migrations.Step[] _chain =
    {
        t =>
        {
            int b = t.FindBlock(_blk);
            if (b < 0 || !t.TryGetColumn(b, 1, out ReadOnlySpan<int> kmh))
            {
                return LoadStatus.MigrationFailed;
            }

            var ms = new float[kmh.Length];
            for (int i = 0; i < ms.Length; i++)
            {
                ms[i] = kmh[i] / 3.6f;
            }

            return t.SetColumn(b, 2, ms) && t.RemoveColumn(b, 1) ? LoadStatus.Ok : LoadStatus.MigrationFailed;
        },
        t =>
        {
            int b = t.FindBlock(_blk);
            return b >= 0 && t.TryGetColumn(b, 2, out ReadOnlySpan<float> ms) && Fill(t, b, ms.Length) ? LoadStatus.Ok : LoadStatus.MigrationFailed;
        },
    };

    private static bool Fill(SaveTables t, int b, int n)
    {
        int[] seven = new int[n];
        Array.Fill(seven, 7);
        return t.SetColumn(b, 3, seven);
    }

    private static SaveTables V1()
    {
        var t = new SaveTables { Header = ContainerTestData.Header() };
        t.AddBlock(ContainerTestData.Aaaa, 1, BlockFlags.Critical);
        t.AddColumn(1, new[] { 1 });
        t.AddBlock(_blk, 1, BlockFlags.Critical);
        t.AddColumn(1, new[] { 36, 72, 0 });
        t.AddColumn(5, new byte[] { 9 });
        t.AddBlock(ContainerTestData.Bbbb, 1, BlockFlags.Critical);
        t.AddColumn(2, new[] { 2 });
        return t;
    }

    [Fact]
    public void Chain_V1ToV3_AppliesEveryStepInOrder()
    {
        SaveTables t = V1();
        Assert.Equal(LoadStatus.Ok, Migrations.Apply(t, 1, 3, _chain, out int applied));
        Assert.Equal(2, applied);
        Assert.Equal(3, t.Header.WorldVersion);

        int b = t.FindBlock(_blk);
        Assert.False(t.TryGetColumn(b, 1, out ReadOnlySpan<int> _));   // выбыл
        Assert.True(t.TryGetColumn(b, 2, out ReadOnlySpan<float> ms));
        Assert.Equal(new[] { 10f, 20f, 0f }, ms.ToArray());
        Assert.True(t.TryGetColumn(b, 3, out ReadOnlySpan<int> seven));
        Assert.Equal(new[] { 7, 7, 7 }, seven.ToArray());
        Assert.True(t.TryGetColumn(b, 5, out ReadOnlySpan<byte> kept));   // чужие столбцы блока не тронуты
        Assert.Equal(new byte[] { 9 }, kept.ToArray());
        Assert.Equal(3, t.ColumnCountOf(b));

        // Соседние блоки целы (индексы столбцов сдвигаются вместе с вставкой/удалением).
        Assert.True(t.TryGetColumn(t.FindBlock(ContainerTestData.Aaaa), 1, out ReadOnlySpan<int> a));
        Assert.Equal(1, a[0]);
        Assert.True(t.TryGetColumn(t.FindBlock(ContainerTestData.Bbbb), 2, out ReadOnlySpan<int> bb));
        Assert.Equal(2, bb[0]);
    }

    [Fact]
    public void Chain_FromMiddleVersion_AppliesOnlyTheTail()
    {
        SaveTables t = V1();
        Assert.Equal(LoadStatus.Ok, Migrations.Apply(t, 1, 2, _chain, out _));
        t.Header.WorldVersion = 2;
        Assert.Equal(LoadStatus.Ok, Migrations.Apply(t, 2, 3, _chain, out int applied));
        Assert.Equal(1, applied);
    }

    [Fact]
    public void Chain_SameVersion_DoesNothing()
    {
        SaveTables t = V1();
        Assert.Equal(LoadStatus.Ok, Migrations.Apply(t, 3, 3, _chain, out int applied));
        Assert.Equal(0, applied);
    }

    [Fact]
    public void Chain_FileNewerThanCore_IsTooNew_AndNothingChanges()
    {
        SaveTables t = V1();
        Assert.Equal(LoadStatus.TooNew, Migrations.Apply(t, 4, 3, _chain, out int applied));
        Assert.Equal(0, applied);
        Assert.True(t.TryGetColumn(t.FindBlock(_blk), 1, out ReadOnlySpan<int> _));
    }

    [Fact]
    public void Chain_MissingStepOrBadSource_IsMigrationFailed()
    {
        Assert.Equal(LoadStatus.MigrationFailed, Migrations.Apply(V1(), 1, 4, _chain, out _));    // нет шага 3 -> 4
        Assert.Equal(LoadStatus.MigrationFailed, Migrations.Apply(V1(), 0, 3, _chain, out _));    // версия 0 не существует

        var broken = new SaveTables { Header = ContainerTestData.Header() };   // нет блока, который ждёт шаг 1 -> 2
        broken.AddBlock(ContainerTestData.Aaaa, 1, BlockFlags.Critical);
        Assert.Equal(LoadStatus.MigrationFailed, Migrations.Apply(broken, 1, 3, _chain, out int applied));
        Assert.Equal(0, applied);
    }

    [Fact]
    public void MigratedTables_AreWrittenAsAValidContainer()
    {
        SaveTables t = V1();
        Assert.Equal(LoadStatus.Ok, Migrations.Apply(t, 1, 3, _chain, out _));
        SaveHeaderInfo h = t.Header;
        long size = SaveWriter.Measure(ref t, out long n) == SaveStatus.Ok ? n : -1;
        byte[] file = new byte[size];
        Assert.Equal(SaveStatus.Ok, SaveWriter.Write(ref t, h, file, out _));
        Assert.Equal(LoadStatus.Ok, SaveReader.Read(file, new[] { ContainerTestData.Aaaa, ContainerTestData.Bbbb, _blk }, out SaveTables? back, out LoadReport rep));
        Assert.Equal(3, rep.FileWorldVersion);
        Assert.True(back!.TryGetColumn(back.FindBlock(_blk), 3, out ReadOnlySpan<int> seven));
        Assert.Equal(3, seven.Length);
    }

    [Fact]
    public void ProductionChain_HasOneStepPerVersionBelowCurrent()
    {
        Assert.True(Migrations.Steps.Length == SaveSchema.WorldVersion - 1);
    }
}
