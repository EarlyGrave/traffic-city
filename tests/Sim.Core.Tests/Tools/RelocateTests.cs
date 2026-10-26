using System;
using System.Linq;
using Sim.Core.Network;
using Sim.Core.Save;

namespace Sim.Core.Tests.Tools;

/// <summary>Перенос ссылок (4.3, T3; player-tools.md §4.2, TL8): реестр столбцов-ссылок схемы сейва против обработчиков <c>Relocate</c> и таблица <see cref="IndexRemap"/>.</summary>
public sealed class RelocateTests
{
    [Fact]
    public void Relocate_Complete_EveryRefColumnHasAHandler_AndNoHandlerIsOrphan()
    {
        foreach (SaveSchema.RefTag t in SaveSchema.Refs)
        {
            Relocate.Handler h = Relocate.Handlers.SingleOrDefault(x => x.Block == t.Block && x.Column == t.Column);
            Assert.True(h.Block != null, $"{t.Block}.{t.Column} ({t.Kind}) без обработчика Relocate: добавьте запись в Relocate.Handlers и код переноса");
            if (t.Kind == RefKind.Lane || t.Kind == RefKind.Connector)
            {
                Assert.False(h.How.StartsWith("не меняется", StringComparison.Ordinal), $"{t.Block}.{t.Column}: ссылка на полосу/connector не может быть «стабильной»");
            }
        }

        foreach (Relocate.Handler h in Relocate.Handlers)
        {
            Assert.True(SaveSchema.Refs.Any(t => t.Block == h.Block && t.Column == h.Column), $"обработчик {h.Block}.{h.Column} без тега в реестре ссылок");
        }

        Assert.Equal(SaveSchema.Refs.Length, Relocate.Handlers.Length);
    }

    [Fact]
    public void IndexRemap_MapsOnlyListedIndexes_AndKeepsNegatives()
    {
        var m = new IndexRemap();
        m.Add(10, 100);
        m.Add(11, -1);
        m.Add(40, 140);
        Assert.Equal(100, m.Map(10));
        Assert.Equal(-1, m.Map(11));
        Assert.Equal(140, m.Map(40));
        Assert.Equal(12, m.Map(12));   // между записями: не трогается
        Assert.Equal(9, m.Map(9));
        Assert.Equal(41, m.Map(41));
        Assert.True(m.Contains(11));
        Assert.False(m.Contains(12));
        m.Clear();
        Assert.Equal(10, m.Map(10));
    }
}
