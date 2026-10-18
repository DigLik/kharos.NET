using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Что берёт подача партии — раздел <c>client</c> партии пилота (games/skirmish.luau): камера, управление, панели,
/// атмосфера, эффекты (имена видов своих разделов). Звук — Audio/AudioSetup.
/// </summary>
internal sealed record ClientProfile(string Camera, string[] Controls, string[] Ui, string[] Atmosphere, string[] Effects)
{
    /// <summary>Партии base по имени записи data/games.json; у чужих партий подачи base нет.</summary>
    public static ClientProfile? Of(string game) => game switch
    {
        "skirmish" => new ClientProfile("rts", ["select", "orders"], ["hud"], ["weather"], ["traces"]),
        _ => null,
    };
}

/// <summary>
/// Клиентская часть base (docs/dotnet.md, Р11): облики видов, HUD, камера, управление, колея, погода и звук — перенос
/// Luau-пилота. Данные берутся из data/*.json при загрузке; подача партии — <see cref="ClientProfile"/>.
/// </summary>
public sealed class BaseClient : ClientMod
{
    private BaseState _state = null!;
    private ClientProfile? _profile;
    private RtsCamera _rts = null!;
    private FollowCamera _follow = null!;
    private Hud _hud = null!;
    private SelectControl _select = null!;
    private OrderControl _orders = null!;
    private Traces _traces = null!;
    private Weather _weather = null!;
    private bool _palette;

    public override void Setup(ClientBuilder client)
    {
        // Облики: только виды, что есть в data (на каждый, которого нет, клиент жалуется в журнал). Остальные чертежи
        // (UnitArt.Units, UnitArt.Turrets) лежат готовыми: вид появится в данных — добавится строка здесь.
        client.Look("units/buggy", UnitArt.Look("buggy", () => _state.Data.Find("units/buggy")?.Mounts() ?? []) with { Icon = 56 });
        client.Look("turrets/gunner", UnitArt.TurretLook("gunner"));
        client.Look("buildings/factory", BuildingArt.Factory());
        foreach (var kind in DepositArt.Types)
            client.Look("deposits/" + kind, DepositArt.Look(kind));
        // Мини-карта пустыни (lands/desert.luau): цвета по биомам (эрг, солончаки, красные пустоши, топи) —
        // [песок, скала, болото, горы], средние тона палитр terrain.frag (PALETTE в minimap.ts).
        client.Minimap(
            0xC29945, 0x63450F, 0x36331A, 0x8C6828,
            0xD1C9B3, 0x5E5C54, 0x4D8585, 0x828078,
            0xB35C29, 0x542414, 0x452112, 0x804024,
            0x857A38, 0x454021, 0x1A3621, 0x645F34);

        // Порядок кадра — порядок пилота: камера → панели → управление → эффекты → атмосфера → звук.
        client.Camera("base.camera", c =>
        {
            Palette(c);
            switch (_profile?.Camera)
            {
                case "rts": _rts.Update(c); break;
                case "follow": _follow.Update(c); break;
            }
        });
        client.Ui("base.ui", c =>
        {
            if (_profile?.Ui.Contains("hud") == true)
                _hud.Draw(c);
        });
        client.Controls("base.controls", c =>
        {
            if (_profile is null)
                return;
            if (_profile.Controls.Contains("select"))
                _select.Update(c);
            if (_profile.Controls.Contains("orders"))
                _orders.Update(c);
        });
        client.Effects("base.effects", c =>
        {
            if (_profile?.Effects.Contains("traces") == true)
                _traces.Update(c);
        });
        client.Atmosphere("base.atmosphere", c =>
        {
            if (_profile?.Atmosphere.Contains("weather") == true)
                _weather.Update(c);
        });
        AudioSetup.Register(client);
    }

    public override void Load(LoadContext ctx)
    {
        _state = new BaseState(ClientData.Load(ctx), new Parts(ctx));
        _profile = ClientProfile.Of(ctx.Game);
        _rts = new RtsCamera(_state);
        _follow = new FollowCamera();
        _hud = new Hud(_state);
        _select = new SelectControl(_state);
        _orders = new OrderControl(_state);
        _traces = new Traces(_state);
        _weather = new Weather(_state);
    }

    /// <summary>Палитра сторон партии (TEAMS из unitArt.ts): своя, чужая, союзная, ничья — тёмный, основной, светлый.</summary>
    private void Palette(ClientContext c)
    {
        if (_palette || _profile is null)
            return;
        _palette = true;
        c.Palette([
            0x1f63d8, 0x5aa9ff, 0xe4f4ff,
            0xb3261e, 0xff6b5a, 0xffe9e4,
            0x1f7a3c, 0x4ec973, 0xe8fff0,
            0x1f63d8, 0x5aa9ff, 0xe4f4ff]);
    }
}
