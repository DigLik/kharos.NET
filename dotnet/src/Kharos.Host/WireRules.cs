using System.Runtime.CompilerServices;
using Kharos.Engine.World;

namespace Kharos.Host;

/// <summary>
/// Что из сущности уходит зрителю (wire.ts: PRIVATE, seenBy). «Свой» — хозяин, его союзники и всякий для ничьей
/// сущности: ему всё, кроме <c>@net none</c>. Чужому — без <c>@net owner</c>, а поля <c>@private</c> — значениями по
/// умолчанию. Исключения TS — явными правилами по именам (П0.1): склад (<c>Stock</c>) ящика с грузом (<c>Drop</c>)
/// виден всем; куда едет MCV (<c>Deploys</c>) — всем, пока он разворачивается (<c>Converting</c>); <c>Health.hit</c> и
/// <c>Scan.wait</c> не уходят никому — меняются без нужды для клиента.
/// </summary>
internal sealed class WireRules
{
    /// <summary>Компонент, открытый всем, пока у сущности есть другой.</summary>
    private static readonly (string Component, string While)[] Open = [("Stock", "Drop"), ("Deploys", "Converting")];

    /// <summary>Поля, которые не уходят никому.</summary>
    private static readonly (string Component, string Field)[] Never = [("Health", "hit"), ("Scan", "wait")];

    private static readonly ConditionalWeakTable<Schema, WireRules> Cache = new();

    private readonly NetMode[] _net;
    private readonly ulong[] _private;
    private readonly ulong[] _never;
    private readonly int[] _openWhile;
    private readonly bool[] _recreates;

    private WireRules(Schema schema)
    {
        var count = schema.Count;
        (_net, _private, _never, _openWhile, _recreates) = (new NetMode[count], new ulong[count], new ulong[count], new int[count], new bool[count]);
        for (var c = 0; c < count; c++)
        {
            _net[c] = schema[c].Net;
            _private[c] = schema[c].PrivateMask;
            _openWhile[c] = -1;
        }
        foreach (var (component, field) in Never)
            if (schema.Find(component) is { } id && schema[id].Field(field) is { } def)
                _never[id] |= 1UL << def.Index;
        foreach (var (component, condition) in Open)
            if (schema.Find(component) is { } id && schema.Find(condition) is { } when)
            {
                _openWhile[id] = when;
                _recreates[when] = true;
            }
        // Сменился хозяин — сменилось и то, что зрителю положено.
        _recreates[Builtin.Owner] = true;
    }

    public static WireRules Of(Schema schema) => Cache.GetValue(schema, static s => new WireRules(s));

    /// <summary>Смена этого компонента меняет, что уходит чужим: сущность — заново целиком.</summary>
    public bool Recreates(int component) => _recreates[component];

    /// <summary>Уходит ли компонент сущности; has — есть ли у неё компонент (для исключений).</summary>
    public bool Shows<T>(int component, bool friend, T has) where T : IHas => _net[component] switch
    {
        NetMode.None => false,
        NetMode.Owner => friend || (_openWhile[component] is >= 0 and var when && has.Has(when)),
        _ => true,
    };

    /// <summary>Поля, которые зрителю не уходят.</summary>
    public ulong Hidden(int component, bool friend) => _never[component] | (friend ? 0 : _private[component]);

    /// <summary>Есть ли у сущности компонент: в мире или в записи.</summary>
    public interface IHas
    {
        bool Has(int component);
    }

    /// <summary>Сущность мира.</summary>
    public readonly struct InWorld(GameWorld world, uint entity) : IHas
    {
        public bool Has(int component) => world.Has(entity, component);
    }
}
