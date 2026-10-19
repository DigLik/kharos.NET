using Base.Shared.Data;
using System.Buffers.Binary;
using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>Где на карте делать шаг: прямоугольник в тайлах.</summary>
internal readonly record struct StepTarget(double X, double Y, double Width, double Height);

/// <summary>
/// Первые шаги по миру — перенос game/firstSteps.ts. Шаг отмечается сам, по реплике, а не по сценарию: есть готовое
/// здание, открыта тема, получена награда. Пройденный дальше отмечает и все до него. Чужие шаги — свои: клиент видит
/// только свой счёт и свои здания.
/// </summary>
internal sealed class StepsReader(BaseState state)
{
    public const string Core = "command";
    private const double DepositSearch = 80;

    public static readonly IReadOnlyList<StepSpec> Steps =
    [
        new("deploy", Deploy: true),
        new("builder", Unit: "builder"),
        new("generator", Building: "generator"),
        new("matter", Building: "matter"),
        new("mine", Building: "mine"),
        new("truck", Unit: "truck"),
        new("lab", Building: "lab"),
        new("metalOre", Research: "metalOre"),
        new("smelter", Building: "smelter"),
        new("metalYard", Building: "metalYard"),
        new("spaceport", Building: "spaceport"),
        new("sale", Sell: true),
    ];

    private bool? _techTree;
    private ComponentRef _science = null!, _site = null!, _footprint = null!;
    private int _kindField = -1, _reserveField = -1;

    private readonly record struct Spot(int X, int Y, string Kind, double Reserve);

    /// <summary>Текущий шаг — первый непройденный; null — пройдены все.</summary>
    public static StepSpec? Current(IReadOnlyList<bool> done)
    {
        for (var i = 0; i < done.Count; i++)
            if (!done[i])
                return Steps[i];
        return null;
    }

    private bool TechTree(ClientContext c)
    {
        if (_techTree is null)
        {
            var tables = BaseTables.Load(name => c.TryData(name, out var bytes) ? bytes.ToArray() : null);
            _techTree = HudRules.Of(c, tables).TechTree;
        }
        return _techTree.Value;
    }

    private bool Init(ClientContext c)
    {
        try
        {
            _science = state.P.Science;
            _site = state.P.Site;
            _footprint = state.P.Footprint;
        }
        catch (Exception)
        {
            return false;
        }
        return true;
    }

    /// <summary>Имена по номерам Proto i32 подряд с приставкой prefix ("rewards/", "research/").</summary>
    private static HashSet<string> Names(ClientContext c, byte[] bytes, string prefix)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var at = 0; at + 4 <= bytes.Length; at += 4)
            if (c.ProtoPath(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at))) is { } path && path.StartsWith(prefix, StringComparison.Ordinal))
                set.Add(path[prefix.Length..]);
        return set;
    }

    private (HashSet<string> Rewards, HashSet<string> Topics) Account(ClientContext c)
    {
        foreach (var entity in c.Each(state.P.Player))
            if (c.Get(entity, state.P.Player) is { } player && player.Number(state.P.Player["id"]) == c.Player)
            {
                var rewards = Names(c, player.Bytes(state.P.Player["rewards"]), "rewards/");
                var topics = c.Get(entity, _science) is { } science ? Names(c, science.Bytes(_science["done"]), "research/") : [];
                return (rewards, topics);
            }
        return ([], []);
    }

    private string? TypeOf(ClientContext c, uint entity, string folder) =>
        c.ProtoPath(c.Proto(entity)) is { } path && path.StartsWith(folder, StringComparison.Ordinal) ? path[folder.Length..] : null;

    /// <summary>Свои здания: вид, готово ли (не стройка).</summary>
    private List<(uint Entity, string Type, bool Ready)> Buildings(ClientContext c)
    {
        var list = new List<(uint, string, bool)>();
        foreach (var entity in c.Each(state.P.Building))
            if (c.Mine(entity) && TypeOf(c, entity, "buildings/") is { } type)
                list.Add((entity, type, !c.Has(entity, _site)));
        return list;
    }

    private bool HasUnit(ClientContext c, string type)
    {
        foreach (var entity in c.Each(state.P.Unit))
            if (c.Mine(entity) && TypeOf(c, entity, "units/") == type)
                return true;
        return false;
    }

    /// <summary>Месторождения слоя, что знает игрок.</summary>
    private List<Spot> Deposits(ClientContext c, double x, double y, double radius)
    {
        var list = new List<Spot>();
        if (state.P.Deposits is not { } layer)
            return list;
        if (_kindField < 0)
        {
            _kindField = IndexOf(layer, "kind");
            _reserveField = IndexOf(layer, "reserve");
        }
        if (_kindField < 0)
            return list;
        foreach (var item in Tools.Items(layer, 0, x, y, radius))
        {
            var kind = (int)item.Fields[_kindField];
            list.Add(new Spot((int)Math.Floor(item.X), (int)Math.Floor(item.Y), (uint)kind < (uint)DepositArt.Types.Length ? DepositArt.Types[kind] : "metal",
                _reserveField < 0 ? 0 : item.Fields[_reserveField]));
        }
        return list;
    }

    private static int IndexOf(LayerRef layer, string field)
    {
        for (var i = 0; i < layer.Fields.Count; i++)
            if (layer.Fields[i] == field)
                return i;
        return -1;
    }

    /// <summary>Готовые шахты на месторождениях вида kind.</summary>
    private List<uint> MinesOn(ClientContext c, List<(uint Entity, string Type, bool Ready)> buildings, string kind)
    {
        var found = new List<uint>();
        var mines = buildings.Where(b => b.Type == "mine" && b.Ready).ToList();
        if (mines.Count == 0)
            return found;
        var spots = Deposits(c, 0, 0, 1e7).Where(spot => spot.Kind == kind).ToList();
        foreach (var (entity, _, _) in mines)
            if (c.Pose(entity) is { } at && spots.Any(spot => at.X >= spot.X && at.X < spot.X + KnownReserve.Size && at.Y >= spot.Y && at.Y < spot.Y + KnownReserve.Size))
                found.Add(entity);
        return found;
    }

    /// <summary>Какие шаги пройдены, по шагу (stepsDone).</summary>
    public bool[] Done(ClientContext c)
    {
        var done = new bool[Steps.Count];
        if (!Init(c))
            return done;
        var (rewards, topics) = Account(c);
        var buildings = Buildings(c);
        bool Built(string type) => buildings.Any(b => b.Type == type && b.Ready);
        bool Researched(string topic) => !TechTree(c) || topics.Contains(topic);
        var met = new Dictionary<string, bool>
        {
            ["deploy"] = rewards.Contains("deploy") || Built(Core),
            ["builder"] = HasUnit(c, "builder"),
            ["generator"] = rewards.Contains("generator") || Built("generator"),
            ["matter"] = rewards.Contains("matter") || Built("matter"),
            // Шахта нужна на металле: на другой руде плавку не изучить.
            ["mine"] = MinesOn(c, buildings, "metal").Count > 0,
            ["truck"] = HasUnit(c, "truck"),
            ["lab"] = Built("lab") || Researched("science"),
            ["metalOre"] = Researched("metalOre"),
            ["smelter"] = Built("smelter") || (TechTree(c) && topics.Contains("trade")),
            ["metalYard"] = rewards.Contains("metalYard") || Built("metalYard"),
            ["spaceport"] = Built("spaceport"),
            ["sale"] = rewards.Contains("sale"),
        };
        for (var i = 0; i < done.Length; i++)
            done[i] = met[Steps[i].Key];
        for (var i = done.Length - 2; i >= 0; i--)
            done[i] |= done[i + 1];
        return done;
    }

    private StepTarget? Footprint(ClientContext c, uint? entity)
    {
        if (entity is not { } id || c.Pose(id) is not { } at)
            return null;
        var (w, h) = c.Get(id, _footprint) is { } f ? (f.Number(_footprint["w"]), f.Number(_footprint["h"])) : (1.0, 1.0);
        return new StepTarget(at.X, at.Y, w, h);
    }

    /// <summary>Своё здание этого вида — готовое или на стройке.</summary>
    private static uint? OwnBuilding(List<(uint Entity, string Type, bool Ready)> buildings, string type)
    {
        uint? site = null;
        foreach (var (entity, kind, ready) in buildings)
        {
            if (kind != type)
                continue;
            if (ready)
                return entity;
            site ??= entity;
        }
        return site;
    }

    private StepTarget? UnitTarget(ClientContext c, string type)
    {
        foreach (var entity in c.Each(state.P.Unit))
            if (c.Mine(entity) && TypeOf(c, entity, "units/") == type && c.Pose(entity) is { } at)
                return new StepTarget(Math.Floor(at.X) - 1, Math.Floor(at.Y) - 1, 3, 3);
        return null;
    }

    /// <summary>Ближайшее к базе известное свободное месторождение вида kind, где ещё есть руда.</summary>
    private StepTarget? NearestDeposit(ClientContext c, string kind, double fromX, double fromY)
    {
        StepTarget? best = null;
        var bestDistance = double.PositiveInfinity;
        var spots = Deposits(c, fromX, fromY, DepositSearch + KnownReserve.Size).Where(spot => spot.Kind == kind).ToList();
        if (spots.Count == 0)
            return null;
        var covered = new List<(double X, double Y, double W, double H)>();
        foreach (var entity in c.Each(state.P.Building))
            if (c.Pose(entity) is { } at)
                covered.Add((at.X, at.Y, c.Get(entity, _footprint) is { } f ? f.Number(_footprint["w"]) : 1, c.Get(entity, _footprint) is { } g ? g.Number(_footprint["h"]) : 1));
        foreach (var spot in spots)
        {
            if (covered.Any(b => spot.X + 0.5 >= b.X && spot.X + 0.5 < b.X + b.W && spot.Y + 0.5 >= b.Y && spot.Y + 0.5 < b.Y + b.H))
                continue;
            if (KnownReserve.Left(c, spot.X, spot.Y, spot.Reserve) is <= 0)
                continue;
            var distance = Math.Sqrt(Math.Pow(spot.X + KnownReserve.Size / 2.0 - fromX, 2) + Math.Pow(spot.Y + KnownReserve.Size / 2.0 - fromY, 2));
            if (distance > DepositSearch || distance >= bestDistance)
                continue;
            best = new StepTarget(spot.X, spot.Y, KnownReserve.Size, KnownReserve.Size);
            bestDistance = distance;
        }
        return best;
    }

    /// <summary>
    /// Где делать шаг: MCV — разворачивать, главное здание — заказывать строителя, грузовик и строиться рядом, месторождение
    /// металла — ставить шахту, шахта на металле — ставить рядом лабораторию и плавильню, плавильня — склад металла, склад —
    /// космопорт, космопорт — продавать. null — показать нечего (исследование или нужное ещё не найдено).
    /// </summary>
    public StepTarget? Target(ClientContext c, string key)
    {
        if (!Init(c))
            return null;
        var buildings = Buildings(c);
        var core = Footprint(c, OwnBuilding(buildings, Core));
        switch (key)
        {
            case "deploy":
                return UnitTarget(c, "mcv");
            case "builder" or "truck" or "generator" or "matter":
                return core;
            case "mine":
                {
                    var origin = core ?? UnitTarget(c, "mcv");
                    return origin is { } b ? NearestDeposit(c, "metal", b.X + b.Width / 2, b.Y + b.Height / 2) : null;
                }
            case "lab" or "smelter":
                return Footprint(c, MinesOn(c, buildings, "metal") is [var mine, ..] ? mine : null);
            case "metalOre":
                return Footprint(c, OwnBuilding(buildings, "lab"));
            case "metalYard":
                return Footprint(c, OwnBuilding(buildings, "smelter"));
            case "spaceport":
                return Footprint(c, OwnBuilding(buildings, "metalYard")) ?? core;
            case "sale":
                return Footprint(c, OwnBuilding(buildings, "spaceport"));
            default:
                return null;
        }
    }
}
