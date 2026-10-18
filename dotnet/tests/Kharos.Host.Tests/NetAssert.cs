using Kharos.Engine.Layers;
using Kharos.Engine.World;
using Kharos.Protocol;

namespace Kharos.Host.Tests;

/// <summary>Сверка реплики с хостом: сущности — те, что видит фильтр; числа — в пределах точности сети.</summary>
public static class NetAssert
{
    /// <summary>Мир и слои реплики равны хосту: сущности (все или видимые фильтру), компоненты, значения, строки, слои зрителя.</summary>
    public static void Same(Host host, Replica replica, IViewFilter? filter = null)
    {
        var world = replica.World!;
        var all = new List<uint>();
        host.World.Entities(all);
        var expected = filter is null ? all : all.Where(filter.Entity).ToList();
        var actual = new List<uint>();
        world.Entities(actual);
        Assert.Equal(expected, actual);
        var shown = expected.ToHashSet();
        var schema = host.World.Schema;
        var format = host.Format;
        for (var c = 0; c < schema.Count; c++)
        {
            Assert.Equal(host.World.Store(c).Entities.ToArray().Count(shown.Contains), world.Store(c).Count);
            foreach (var entity in host.World.Store(c).Entities)
            {
                if (!shown.Contains(entity))
                    continue;
                Assert.True(world.Has(entity, c), $"у {entity} нет {schema[c].Name}");
                foreach (var field in schema[c].Fields)
                {
                    var (mine, theirs) = (host.World.Get(entity, c, field.Index), world.Get(entity, c, field.Index));
                    if (field.Kind == FieldKind.Number)
                        Assert.True(Math.Abs(mine.Number - theirs.Number) <= format.Error(c, field.Index) * (1 + 1e-9) + 1e-12,
                            $"{entity} {schema[c].Name}.{field.Name}: хост {mine.Number}, реплика {theirs.Number}");
                    else
                        Assert.Equal(mine, theirs);
                }
            }
        }
        for (var id = 0; id < host.Layers.Count; id++)
        {
            var spec = host.Layers.Spec(id);
            var owner = spec.Scope == LayerScope.World ? LayerOwner.World : LayerOwner.Player(replica.Player);
            Assert.Equal(host.Layers.Blocks(id, owner), replica.Layers!.Blocks(replica.Layers.Id(spec.Name), owner));
        }
    }
}
