using Kharos.Mod.Sdk;

namespace Base.Host;

/// <summary>
/// Мод base — хост-часть игры Kharos (docs/dotnet.md, Н3): перенос правил Luau-пилота (mods/base) на C# в wasm.
/// Системы, события и приказы объявлены в порядке видов пилота (по разделам и именам) и под его именами —
/// «раздел/вид.система», ключ — после точки (<c>drive</c>, <c>separate</c>), чтобы чужие моды ставились до/после
/// и заменяли по имени. Числа правил — в data/*.json, компоненты — в mod.json.
/// <list type="table">
/// <item><term>games/skirmish</term><description><see cref="Skirmish"/>: player_joined</description></item>
/// <item><term>mechanics/crowd</term><description><see cref="Crowd"/>: система separate</description></item>
/// <item><term>mechanics/deposits</term><description><see cref="Deposits"/>: start, deposits.cell</description></item>
/// <item><term>mechanics/mounts</term><description><see cref="Mounts"/>: spawned, система follow</description></item>
/// <item><term>mechanics/orders</term><description><see cref="Orders"/>: приказы move, stop</description></item>
/// <item><term>mechanics/players</term><description><see cref="Players"/>: player_joined, система income</description></item>
/// <item><term>mechanics/production</term><description><see cref="Production"/>: система produce, приказы produce, cancel, rally</description></item>
/// <item><term>moves/wheels</term><description><see cref="Wheels"/>: система drive</description></item>
/// </list>
/// </summary>
public sealed class BaseMod : Mod
{
    private readonly Skirmish _skirmish;
    private readonly Crowd _crowd;
    private readonly Deposits _deposits;
    private readonly Mounts _mounts;
    private readonly Orders _orders;
    private readonly Players _players;
    private readonly Production _production;
    private readonly Wheels _wheels;

    public BaseMod()
    {
        _skirmish = new Skirmish(this);
        _crowd = new Crowd(this);
        _deposits = new Deposits(this);
        _mounts = new Mounts(this);
        _orders = new Orders(this);
        _players = new Players(this);
        _production = new Production(this);
        _wheels = new Wheels();
    }

    /// <summary>Компоненты схемы.</summary>
    internal Parts P { get; private set; } = null!;

    /// <summary>Данные мода (data/*.json после патчей).</summary>
    internal BaseData D { get; private set; } = null!;

    /// <summary>Идущая партия (запись data/games.json): правила берутся только у неё.</summary>
    internal string Game { get; private set; } = "";

    public override void Setup(ModBuilder mod)
    {
        mod.System("mechanics/crowd.separate", _crowd)
            .Query("Position", "Unit", "Drive")
            .Write("Position")
            .After("drive");
        mod.System("mechanics/mounts.follow", _mounts)
            .Query("Attached", "Position", "Facing", "Turret")
            .Write("Position", "Facing")
            .After("drive", "separate");
        mod.System("mechanics/players.income", _players)
            .Query("Player")
            .Write("Player")
            .Every(20);
        mod.System("mechanics/production.produce", _production)
            .Query("Producer", "Position", "Owner")
            .Write("Producer");
        mod.System("moves/wheels.drive", _wheels)
            .Query("Position", "Facing", "Drive")
            .Where("Drive", "move", "wheels")
            .Write("Position", "Facing", "Drive");

        mod.On("start", _deposits.OnStart);
        mod.On(Deposits.CellEvent, _deposits.OnCell);
        mod.On("player_joined", _skirmish.OnJoined);
        mod.On("player_joined", _players.OnJoined);
        mod.On("spawned", _mounts.OnSpawned);

        mod.Order("move", _orders.Move);
        mod.Order("stop", _orders.Stop);
        mod.Order("produce", _production.Produce);
        mod.Order("cancel", _production.Cancel);
        mod.Order("rally", _production.Rally);
    }

    public override void Load(LoadContext ctx)
    {
        P = new Parts(ctx);
        D = BaseData.Load(ctx);
        Game = ctx.Game;
    }
}
