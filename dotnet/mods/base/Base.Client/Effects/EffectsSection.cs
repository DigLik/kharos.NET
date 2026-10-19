using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Раздел Effects: эффекты мира (docs/dotnet-n6.md, К1). Здания — запись Paint (Effects/Paint.cs); колея — со слоя
/// следов движка (Effects/Traces.cs), отметины и остовы оттуда же (Wrecks.cs), дропы (Drops.cs); трубы (Flow.cs); бой —
/// выстрелы, взрывы, огни, дым и пыль (Combat.cs), ракеты и перехватчики (Missiles.cs); лучи работы и подачи, купола
/// щитов (Links.cs); энергия и тревоги — молнии, значки, полосы (Power.cs, Hud/Lacks.cs). Эффекты — часть игры base:
/// идут в любой её партии; колея — по подаче партии (client.effects: traces), у партии без подачи — тоже.
/// TODO(К2): Effects/Cursor.cs.
/// </summary>
internal sealed class EffectsSection : Section
{
    private Traces _traces = null!;
    private Fx _fx = null!;
    private Paint _paint = null!;
    private Economy _economy = null!;
    private Lacks _lacks = null!;
    private Power _power = null!;
    private Smoke _smoke = null!;
    private Combat _combat = null!;
    private Missiles _missiles = null!;
    private Links _links = null!;
    private Flow _flow = null!;
    private Wrecks _wrecks = null!;
    private Drops _drops = null!;

    public override void Setup(ClientBuilder client) => client.Effects("base.effects", Frame);

    public override void Load(BaseState state)
    {
        base.Load(state);
        _traces = new Traces(state);
        _fx = new Fx(state);
        _paint = new Paint(_fx);
        _economy = new Economy(_fx, _paint);
        _lacks = new Lacks(_fx, _paint);
        _power = new Power(_fx, _economy, _lacks, _paint);
        _smoke = new Smoke(_fx);
        _combat = new Combat(_fx, _smoke);
        _missiles = new Missiles(_fx, _smoke);
        _links = new Links(_fx, _lacks);
        _flow = new Flow(_fx, _paint);
        _wrecks = new Wrecks(_fx);
        _drops = new Drops(_fx);
    }

    /// <summary>Партия base (запись в data/games.json): чужая партия эффектов base не получает.</summary>
    private bool BaseGame(ClientContext c) => Profile is not null || State.Data.Game(c.Game) is not null;

    public override void Frame(ClientContext c)
    {
        if (!BaseGame(c))
            return;
        var traces = Profile is null || Profile.Effects.Contains("traces");
        if (!_fx.Begin(c))
        {
            if (traces)
                _traces.Update(c);
            return;
        }
        // Сначала то, что считается по миру (энергия, нехватки), — от него зависят облики зданий, лучи и значки.
        _economy.Update();
        _paint.Update(_economy);
        _lacks.Update();
        // Земля: отметины, остовы, дропы; трубы.
        _wrecks.Draw();
        // Колея — со слоя следов движка, как в TS; без него — по ходу юнитов на клиенте.
        if (traces && _wrecks.Works)
            _traces.Receive(c, _wrecks.Tracks, _fx.Step);
        else if (traces)
            _traces.Update(c);
        _drops.Draw();
        _flow.Draw();
        // Бой: дым и огни, затем пыль, дым, взрывы, выстрелы, ракеты, метки.
        _combat.Emit(_missiles);
        _links.Emit();
        _combat.Dust();
        var atmosphere = c.Atmosphere;
        _smoke.DrawDust(atmosphere.WindX, atmosphere.WindY);
        _smoke.DrawSmoke(atmosphere.WindX, atmosphere.WindY, Math.Clamp(atmosphere.Light, 0, 1));
        _combat.DrawBlasts();
        _combat.DrawShots();
        _missiles.Draw();
        _combat.DrawMarks();
        _links.Draw();
        // Тревоги и полосы — поверх всего.
        _power.Update();
    }
}
