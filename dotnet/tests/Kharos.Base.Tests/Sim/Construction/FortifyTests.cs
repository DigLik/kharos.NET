using Base.Shared.Rules;

namespace Kharos.Base.Tests.Construction;

/// <summary>Укрепление (packages/tests/fortify.test.ts): правило Base.Shared, без партии.</summary>
public sealed class FortifyTests
{
    [Fact(DisplayName = "укрепление: разные схемы, ни одной постройки на главном здании и ни одной клетки дважды, всегда есть турели")]
    public void Fortification()
    {
        long seed = 1;
        double Random() => (seed = seed * 16807 % 2147483647) / 2147483647.0;
        var shapes = new HashSet<string>();
        for (var i = 0; i < 50; i++)
        {
            var pieces = Fortify.Fortification(Random);
            var keys = pieces.Select(piece => $"{piece.U},{piece.V}").ToList();
            Assert.Equal(keys.Count, keys.Distinct().Count());
            foreach (var piece in pieces)
                Assert.True(Math.Abs(piece.U) > 2 || Math.Abs(piece.V) > 2);
            Assert.Contains(pieces, piece => piece.Type != "wall");
            shapes.Add(string.Join(' ', keys.Order(StringComparer.Ordinal)));
        }
        Assert.True(shapes.Count > 40, $"{shapes.Count}");
    }
}
