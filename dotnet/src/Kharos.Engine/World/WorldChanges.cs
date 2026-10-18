namespace Kharos.Engine.World;

/// <summary>Поля компонента сущности, изменённые с прошлого раза: бит i маски — поле с <see cref="FieldDef.Index"/> i.</summary>
public readonly record struct FieldChange(uint Entity, int Component, ulong Mask);

/// <summary>Компонент сущности: добавлен или снят.</summary>
public readonly record struct ComponentChange(uint Entity, int Component);

/// <summary>
/// Что изменилось в мире с прошлого <see cref="GameWorld.TakeChanges"/> — для дельт сети и журнала. Списки — по
/// порядку событий; одна сущность может и появиться, и пропасть за раз. Добавленный компонент уходит целиком, его
/// поля в <see cref="Fields"/> могут быть и не быть. Удаление сущности не перечисляет её компоненты в
/// <see cref="Removed"/>.
/// </summary>
public sealed class WorldChanges
{
    public List<uint> Spawned { get; } = [];
    public List<uint> Destroyed { get; } = [];
    public List<ComponentChange> Added { get; } = [];
    public List<ComponentChange> Removed { get; } = [];

    /// <summary>Изменённые поля: по компонентам в порядке номеров, внутри — по порядку первой правки.</summary>
    public List<FieldChange> Fields { get; } = [];

    public bool IsEmpty => Spawned.Count == 0 && Destroyed.Count == 0 && Added.Count == 0 && Removed.Count == 0 && Fields.Count == 0;

    public void Clear()
    {
        Spawned.Clear();
        Destroyed.Clear();
        Added.Clear();
        Removed.Clear();
        Fields.Clear();
    }
}
