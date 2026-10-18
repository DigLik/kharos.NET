using Kharos.Engine.Layers;

namespace Kharos.Protocol;

/// <summary>
/// Туман войны (crates/world/src/vision.rs, fogPass.ts): обзор игрока — два его слоя-сетки: <see cref="Sight"/> (виден
/// сейчас, выводится из глаз, не сохраняется) и <see cref="Explored"/> (разведан, сохраняется). Их пишет хост, клиенту
/// они приходят изменёнными чанками, как любой слой игрока.
/// </summary>
public static class Fog
{
    public const string Sight = "sight";
    public const string Explored = "explored";

    /// <summary>Непрозрачность тумана над тайлом: неразведанный, разведанный, видимый.</summary>
    public const double Hidden = 0.94, Remembered = 0.5, Visible = 0;

    /// <summary>Объявления слоёв обзора.</summary>
    public static IReadOnlyList<LayerSpec> Layers { get; } =
    [
        new LayerSpec { Name = Sight, Scope = LayerScope.Player, Channels = [Channel.U8], Save = false, Send = true },
        new LayerSpec { Name = Explored, Scope = LayerScope.Player, Channels = [Channel.U8], Save = true, Send = true },
    ];

    /// <summary>Туман по значениям слоёв: виден — 0, разведан — 0,5, иначе 0,94.</summary>
    public static double Alpha(bool sees, bool explored) => sees ? Visible : explored ? Remembered : Hidden;
}

/// <summary>
/// Что зритель получает: сущности и слои. <see cref="FullView"/> — всё; фильтр по обзору — VisionView хоста.
/// </summary>
public interface IViewFilter
{
    /// <summary>Игрок-зритель.</summary>
    uint Player { get; }

    /// <summary>Видна ли сущность зрителю сейчас.</summary>
    bool Entity(uint entity);

    /// <summary>Уходит ли изменение слоя зрителю.</summary>
    bool Layer(LayerSpec spec, LayerOwner owner);

    /// <summary>
    /// Помнит ли зритель сущность, ушедшую из виду (здание, месторождение): она остаётся у клиента какой её видели
    /// последней, без изменений, а погибшая — пока зритель не увидит её место.
    /// </summary>
    bool Remembers(uint entity) => false;

    /// <summary>Видит ли зритель место (x, y) сейчас.</summary>
    bool Sees(double x, double y) => true;
}

/// <summary>Всё видно: сущности все, слои мира с send и свои слои игрока с send.</summary>
public sealed class FullView(uint player) : IViewFilter
{
    public uint Player { get; } = player;

    public bool Entity(uint entity) => true;

    public bool Layer(LayerSpec spec, LayerOwner owner) => spec.Send && (owner.IsWorld || owner.PlayerId == Player);
}
