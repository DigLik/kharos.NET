using Base.Shared;
using Kharos.Engine.World;

namespace Kharos.Base.Scenario;

// Чтение и запись компонентов мира по именам из Base.Shared.Names: sim.Num(e, Names.Health.Name, Names.Health.Value) или
// короче — sim.Num(e, "Health.value"). Мир лежит на стороне хоста в обоих режимах (CLR и wasm), поэтому прямая запись
// для подготовки сцены работает одинаково; механики, которые должен выполнить мод (спавн, кредиты), идут правками edit.*.
public sealed partial class Sim
{
    private readonly Dictionary<string, (int Component, int Field)> _paths = new(StringComparer.Ordinal);

    /// <summary>Номер компонента по имени (<c>Names.Health.Name</c>).</summary>
    public int Component(string component) => World.Schema.Id(component);

    /// <summary>Компонент и поле по пути «Компонент.поле» (кэш).</summary>
    private (int Component, int Field) Field(string path)
    {
        if (_paths.TryGetValue(path, out var found))
            return found;
        var dot = path.IndexOf('.');
        if (dot <= 0)
            throw new ArgumentException($"путь «{path}»: ждали «Компонент.поле»", nameof(path));
        var id = World.Schema.Id(path[..dot]);
        return _paths[path] = (id, World.Schema[id].FieldIndex(path[(dot + 1)..]));
    }

    // ===== Сущности =====

    /// <summary>Жива ли сущность.</summary>
    public bool Alive(uint entity) => World.Alive(entity);

    /// <summary>Есть ли у сущности компонент.</summary>
    public bool Has(uint entity, string component) => World.Has(entity, Component(component));

    /// <summary>Сущности со всеми этими компонентами, по возрастанию номеров.</summary>
    public List<uint> With(params string[] components)
    {
        var list = new List<uint>();
        World.Query([.. components.Select(Component)], list);
        list.Sort();
        return list;
    }

    /// <summary>Сущности с компонентом, чьё числовое поле равно значению (Owner.player == 1).</summary>
    public List<uint> With(string component, string field, double value)
    {
        var id = Component(component);
        var index = World.Schema[id].FieldIndex(field);
        var list = new List<uint>();
        World.Query([id], list);
        list.RemoveAll(entity => World.Number(entity, id, index) != value);
        list.Sort();
        return list;
    }

    /// <summary>Сущностей со всеми этими компонентами.</summary>
    public int Count(params string[] components) => With(components).Count;

    /// <summary>Свои сущности игрока с этими компонентами (Owner.player == player).</summary>
    public List<uint> Of(uint player, params string[] components)
    {
        var owner = Component(Names.Owner.Name);
        var field = World.Schema[owner].FieldIndex(Names.Owner.Player);
        var list = With([Names.Owner.Name, .. components]);
        list.RemoveAll(entity => World.Number(entity, owner, field) != player);
        return list;
    }

    // ===== Поля =====

    /// <summary>Числовое поле: <c>Num(e, "Health", "value")</c>.</summary>
    public double Num(uint entity, string component, string field)
    {
        var id = Component(component);
        return World.Number(entity, id, World.Schema[id].FieldIndex(field));
    }

    /// <summary>Числовое поле по пути «Компонент.поле».</summary>
    public double Num(uint entity, string path)
    {
        var (component, field) = Field(path);
        return World.Number(entity, component, field);
    }

    /// <summary>Строковое поле (имя вида, ключ, пустая строка — нет).</summary>
    public string Text(uint entity, string path)
    {
        var (component, field) = Field(path);
        return World.Get(entity, component, field).Text ?? "";
    }

    /// <summary>Байтовое поле (путь, очередь, список).</summary>
    public byte[] Bytes(uint entity, string path)
    {
        var (component, field) = Field(path);
        return World.GetBytes(entity, component, field).ToArray();
    }

    /// <summary>Записать числовое поле (подготовка сцены). Нет компонента — <see cref="Add"/> сначала.</summary>
    public void Set(uint entity, string path, double value)
    {
        var (component, field) = Field(path);
        World.SetNumber(entity, component, field, value);
    }

    /// <summary>Записать строковое поле.</summary>
    public void Set(uint entity, string path, string value)
    {
        var (component, field) = Field(path);
        World.Set(entity, component, field, FieldValue.Of(value));
    }

    /// <summary>Записать байтовое поле.</summary>
    public void Set(uint entity, string path, byte[] value)
    {
        var (component, field) = Field(path);
        World.SetBytes(entity, component, field, value);
    }

    /// <summary>Добавить сущности компонент (поля — по умолчанию схемы).</summary>
    public void Add(uint entity, string component) => World.Add(entity, Component(component));

    /// <summary>Убрать компонент.</summary>
    public void Remove(uint entity, string component) => World.Remove(entity, Component(component));

    // ===== Частые вещи =====

    /// <summary>Место сущности (Position).</summary>
    public (double X, double Y) At(uint entity) => (Num(entity, Names.Position.Name, Names.Position.X), Num(entity, Names.Position.Name, Names.Position.Y));

    /// <summary>Хозяин сущности (Owner.player); у ничьей — 0.</summary>
    public uint OwnerOf(uint entity) => Has(entity, Names.Owner.Name) ? (uint)Num(entity, Names.Owner.Name, Names.Owner.Player) : 0;

    /// <summary>Прочность (Health.value); нет компонента — NaN.</summary>
    public double HealthOf(uint entity) => Has(entity, Names.Health.Name) ? Num(entity, Names.Health.Name, Names.Health.Value) : double.NaN;

    /// <summary>Кошелёк игрока: сущность с Player.id == player; нет — 0.</summary>
    public uint Wallet(uint player) => With(Names.Player.Name, Names.Player.Id, player).FirstOrDefault();

    /// <summary>Кредиты игрока; нет кошелька — 0.</summary>
    public double Credits(uint player) => Wallet(player) is var wallet && wallet != 0 ? Num(wallet, Names.Player.Name, Names.Player.Credits) : 0;

    /// <summary>Расстояние между двумя точками.</summary>
    public static double Distance((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
