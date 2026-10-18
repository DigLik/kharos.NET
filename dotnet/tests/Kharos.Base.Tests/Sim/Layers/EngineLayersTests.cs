using Kharos.Engine.Layers;
using Kharos.Engine.Recall;
using Kharos.Engine.Traces;

namespace Kharos.Base.Tests.Layers;

/// <summary>Слои base (data/layers.json) для систем движка Д3: следы (engine.traces), память покрытия (engine.recallPaving) и слой земли.</summary>
public sealed class EngineLayersTests
{
    private static LayerSet Layers() => new(BaseGame.Content().Layers);

    [Fact]
    public void BaseLayersFitEngineTracesAndPavingMemory()
    {
        var layers = Layers();
        Assert.NotNull(TraceLayer.Create(layers, () => 0, _ => false));
        var paving = PavingMemory.Create(layers);
        Assert.NotNull(paving);
        Assert.Equal(layers.Id("paving"), paving.Layer);
        // Земля — слой из четырёх байтов тайла; инструмент Layer читает его из карты, а не из блоков.
        var land = layers.Spec(layers.Id("land"));
        Assert.Equal([Channel.U8, Channel.U8, Channel.U8, Channel.U8], land.Channels);
        Assert.True(land.Remember);
        Assert.Equal(["kind", "reserve"], layers.Spec(layers.Id("deposits")).Fields.Select(field => field.Name));
    }
}
