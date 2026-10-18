using Kharos.Engine.Layers;
using Kharos.Engine.World;
using Kharos.Protocol;

namespace Kharos.Host;

/// <summary>
/// Что игрок видит (host.ts: shownBy): своё и союзное — всегда; чужое и ничьё с местом на карте — когда оно в его
/// обзоре сейчас; без места — ничьё (память модов, общие сущности) видно всем, чужое — никому. Слои — мира с send и
/// свои слои игрока (союзников — у слоёв с allies). Помнит сущности с компонентами <see cref="HostOptions.Remember"/>.
/// Тумана у партии нет — видно всё.
/// </summary>
public sealed class VisionView(Host host, uint player) : IViewFilter
{
    public uint Player { get; } = player;

    /// <summary>Союзник ли игрок; null — союзов нет (их объявят моды, Н5).</summary>
    public Func<uint, bool>? Allied { get; init; }

    public bool Entity(uint entity)
    {
        var world = host.World;
        if (!world.Alive(entity))
            return false;
        var owner = world.TryNumber(entity, Builtin.Owner, 0, out var number) && number >= 1 && number <= uint.MaxValue ? (uint)number : 0;
        if (owner != 0 && (owner == Player || Allied?.Invoke(owner) == true))
            return true;
        if (!world.TryNumber(entity, Builtin.Position, 0, out var x) || !world.TryNumber(entity, Builtin.Position, 1, out var y))
            return owner == 0;
        return Sees(x, y);
    }

    public bool Layer(LayerSpec spec, LayerOwner owner) =>
        spec.Send && (owner.IsWorld || owner.PlayerId == Player || (spec.Allies && Allied?.Invoke(owner.PlayerId) == true));

    public bool Remembers(uint entity) => host.Remembered(entity);

    public bool Sees(double x, double y) => host.Vision is not { } vision || vision.Sees(Player, x, y);
}
