using System.Buffers.Binary;
using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;
using MemoryPack;

namespace Hello;

/// <summary>
/// Пробный клиентский мод (docs/dotnet.md, Н3): облик ровера (units/rover) на холсте, камера (к своим юнитам, клавиши,
/// колесо), HUD — счёт юнитов, мини-карта и кнопка «hello.spawn» со звуком голоса, колея за своими юнитами, отметка
/// юнита под указателем, звук прибытия по сигналу хоста hello.beep, ветер и свет.
/// </summary>
public sealed class HelloClient : ClientMod
{
    /// <summary>Номер кнопки HUD (по нему тест находит её место).</summary>
    public const string SpawnButton = "hello:spawn";

    private const uint Ink = 0x0b111b;

    private readonly Dictionary<uint, (double X, double Y)> _trails = [];
    private ComponentRef _drive = null!;
    private int _rover = -1;
    private bool _centered;
    private int _units;
    private int _clicks;

    public override void Setup(ClientBuilder client)
    {
        client.Look("units/rover", Rover);
        client.Sample("hello:blip", Bus.Interface, 2, Blip);
        client.Voice("hello:chime", Chime(660));
        client.Signal("hello.beep", OnBeep);
        client.Camera("hello.camera", Camera);
        client.Ui("hello.hud", Hud);
        client.Controls("hello.pick", Pick);
        client.Effects("hello.trails", Trails);
        client.Atmosphere("hello.weather", c =>
        {
            if (!_active)
                return;
            c.Atmosphere.Light = 1;
            c.Atmosphere.WindX = 0.6f;
            c.Atmosphere.WindY = 0.2f;
        });
    }

    /// <summary>
    /// Идёт ли своя партия (hello или никакая — стенды тестов). В чужой (skirmish мода base рядом в dotnet/mods) камера,
    /// HUD, выбор, колея и погода мода молчат: интерфейсы двух модов не смешиваются.
    /// </summary>
    private bool _active = true;

    public override void Load(LoadContext ctx)
    {
        _drive = ctx.Component("Drive");
        _active = ctx.Game is "" or "hello";
    }

    /// <summary>Ровер: колёса, корпус и кабина цветом стороны, фары; 64 направления × 2 фазы хода.</summary>
    public static LookSpec Rover => new(DrawRover)
    {
        Directions = 64,
        Phases = 2,
        Step = 1 / 8f,
        Lamps = [new Lamp(9, -3, 3), new Lamp(9, 3, 3)],
        Beam = new Beam(Along: 9, Length: 48, Near: 3, Spread: 0.3f, Level: 0.8f),
        Portrait = new PortraitSpec(32, 32, canvas => DrawRover(canvas, 0, 0)),
    };

    /// <summary>Кадр ровера направлением angle в фазе phase.</summary>
    public static void DrawRover(Canvas canvas, double angle, int phase)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        (double X, double Y) At(double along, double across) => (c * along - s * across, s * along + c * across);
        foreach (var (along, across) in new[] { (-6.0, -6.0), (6.0, -6.0), (-6.0, 6.0), (6.0, 6.0) })
        {
            var (x, y) = At(along + (phase % 2 == 0 ? 0 : 0.5), across);
            canvas.Circle(x, y, 2.6, Ink);
        }
        double[] Quad(double a0, double c0, double a1, double c1)
        {
            var (p0, p1, p2, p3) = (At(a0, c0), At(a1, c0), At(a1, c1), At(a0, c1));
            return [p0.X, p0.Y, p1.X, p1.Y, p2.X, p2.Y, p3.X, p3.Y];
        }
        canvas.Poly(Quad(-9, -5, 9, 5), Canvas.Toned(0x1f63d8, 1));
        canvas.Poly(Quad(-4, -3.5, 5, 3.5), Canvas.Toned(0x5aa9ff, 2));
        var (fx, fy) = At(-9, 0);
        var (bx, by) = At(-4, 0);
        canvas.Line(fx, fy, bx, by, 1.5, Canvas.Toned(0x10358f, 0));
        var (lx, ly) = At(9, -3);
        var (rx, ry) = At(9, 3);
        canvas.Pixel(lx, ly, 0xfff1b8).Pixel(rx, ry, 0xfff1b8);
    }

    /// <summary>Короткий писк: синус с быстрым спадом; второй вариант — на кварту выше.</summary>
    private static float[][] Blip(int rate, int variant)
    {
        var length = rate / 12;
        var samples = new float[length];
        var frequency = variant == 0 ? 880.0 : 880.0 * 4 / 3;
        for (var i = 0; i < length; i++)
        {
            var t = (double)i / rate;
            samples[i] = (float)(Math.Sin(2 * Math.PI * frequency * t) * Math.Exp(-t * 40) * 0.5);
        }
        return [samples];
    }

    /// <summary>Голос щелчка: треугольник с огибающей через фильтр.</summary>
    public static VoiceGraph Chime(float frequency)
    {
        var s = new VoiceGraphBuilder();
        var tone = s.Osc(Wave.Triangle, frequency);
        var env = s.Env(attack: 0.005f, decay: 0.08f, sustain: 0.3f, release: 0.1f, peak: 0.6f);
        tone.To(s.Filter(FilterType.Lowpass, 2400, 0.8f)).To(env).To(s.Out);
        return s.Build();
    }

    /// <summary>Хост: юнит доехал (entity:u32, x:f64, y:f64) — писк в мире.</summary>
    private static void OnBeep(ClientContext c, HostSignal signal)
    {
        if (signal.Payload.Length < 20)
            return;
        var x = BinaryPrimitives.ReadDoubleLittleEndian(signal.Payload.AsSpan(4));
        var y = BinaryPrimitives.ReadDoubleLittleEndian(signal.Payload.AsSpan(12));
        c.Sound("hello:blip", x, y, 0.5f);
    }

    /// <summary>Камера: в первом кадре — к своим юнитам; стрелки и WASD — сдвиг, колесо — масштаб.</summary>
    private void Camera(ClientContext c)
    {
        if (!_active)
            return;
        var camera = c.Camera;
        if (!_centered)
        {
            var mine = c.Each(_drive).Where(c.Mine).ToArray();
            if (mine.Length > 0)
            {
                var poses = new (double X, double Y, double Angle)[mine.Length];
                c.Poses(mine, poses);
                camera.X = poses.Average(p => p.X);
                camera.Y = poses.Average(p => p.Y);
                camera.Zoom = 48;
                _centered = true;
            }
        }
        var speed = 600 / camera.Zoom * c.Dt;
        if (c.Input.Keys.Contains("a") || c.Input.Keys.Contains("left")) camera.X -= speed;
        if (c.Input.Keys.Contains("d") || c.Input.Keys.Contains("right")) camera.X += speed;
        if (c.Input.Keys.Contains("w") || c.Input.Keys.Contains("up")) camera.Y -= speed;
        if (c.Input.Keys.Contains("s") || c.Input.Keys.Contains("down")) camera.Y += speed;
        if (c.Input.Wheel != 0 && !c.Input.OverUi)
            camera.Zoom = Math.Clamp(camera.Zoom * Math.Pow(1.15, c.Input.Wheel), 8, 128);
    }

    /// <summary>HUD: счёт юнитов, мини-карта, кнопка «ещё пять» (приказ hello.spawn у центра камеры) со звуком.</summary>
    private void Hud(ClientContext c)
    {
        if (!_active)
            return;
        var ui = c.Ui;
        _units = c.Each(_drive).Length;
        if (_rover < 0)
            _rover = c.ProtoId("units/rover");
        ui.Box(new Style { Id = "hello:top", Anchor = new Anchor(Left: 0, Right: 0, Top: 0), Height = 34, Skin = Skin.Panel, Padding = (4, 12), Direction = Direction.Row, Align = Align.Center, Gap = 10 }, () =>
        {
            ui.Image(_rover, 24, 24);
            ui.Text($"Юнитов: {_units}", new TextStyle { Font = FontName.Title, Color = Color.Hex(0xf0c95a), Shadow = Color.Ink, Lift = 2 });
            ui.Space(grow: 1);
            ui.Text($"Тик {c.Tick}", new TextStyle { Color = Color.Hex(0x8fa9c2) });
        });
        ui.Box(new Style { Id = "hello:panel", Anchor = new Anchor(Left: 8, Bottom: 8), Skin = Skin.Panel, Padding = 8, Gap = 8, Direction = Direction.Row, Align = Align.End }, () =>
        {
            ui.Box(new Style { Id = "hello:map", Width = 120, Height = 120, Skin = Skin.Well, Padding = 4 }, () => ui.Minimap(112, 112));
            ui.Box(new Style { Gap = 6 }, () =>
            {
                ui.Text($"Камера {c.Camera.X:0.0}, {c.Camera.Y:0.0}", new TextStyle { Font = FontName.Small, Color = Color.Hex(0xb4c9dd) });
                var click = ui.Button(SpawnButton, Style.Button with { Height = 30, Skin = Skin.ButtonPrimary }, () =>
                {
                    ui.Coin();
                    ui.Text("Ещё пять", new TextStyle { Shadow = Color.Ink });
                });
                ui.Tip(SpawnButton, "Пять роверов у центра экрана");
                if (click is not null)
                {
                    _clicks++;
                    c.Send("hello.spawn", MemoryPackSerializer.Serialize(new SpawnOrder { Count = 5, X = c.Camera.X, Y = c.Camera.Y, Spread = 3 }));
                    c.Audio.Voice("hello:chime", new VoiceOptions { Bus = Bus.Interface, Length = 0.2f });
                    c.Audio.Voice(Chime(990 + _clicks % 3 * 110), new VoiceOptions { Bus = Bus.Interface, Length = 0.15f });
                    c.Audio.Play("hello:blip", 0.4f);
                }
            });
        });
    }

    /// <summary>Отметка юнита под указателем (инструмент Pick) — кольцо поверх мира.</summary>
    private void Pick(ClientContext c)
    {
        if (!_active)
            return;
        if (!c.Input.Inside || c.Input.OverUi || c.Pick(c.Input.WorldX, c.Input.WorldY) is not { } entity || c.Pose(entity) is not { } pose)
            return;
        c.Draw.Circle(pose.X, pose.Y, 0.8, c.Mine(entity) ? 0x5aa9ffu : 0xff6b5au, width: 2, alpha: 0.9f);
    }

    /// <summary>Колея: свой юнит, проехав полтайла, оставляет след под каждым бортом; из-под колёс — пыль.</summary>
    private void Trails(ClientContext c)
    {
        if (!_active)
            return;
        var units = c.Each(_drive);
        var poses = new (double X, double Y, double Angle)[units.Length];
        c.Poses(units, poses);
        for (var i = 0; i < units.Length; i++)
        {
            var (x, y, angle) = poses[i];
            if (double.IsNaN(x))
                continue;
            if (!_trails.TryGetValue(units[i], out var last))
            {
                _trails[units[i]] = (x, y);
                continue;
            }
            var moved = Math.Sqrt((x - last.X) * (x - last.X) + (y - last.Y) * (y - last.Y));
            if (moved < 0.5)
                continue;
            _trails[units[i]] = (x, y);
            if (moved > 1.5)
                continue;
            var (sx, sy) = (-Math.Sin(angle) * 4.5 / 16, Math.Cos(angle) * 4.5 / 16);
            foreach (var side in (ReadOnlySpan<int>)[-1, 1])
                c.Decal(last.X + sx * side, last.Y + sy * side, x + sx * side, y + sy * side, 3 / 16f, (0.16f, 0.11f, 0.06f), opacity: 0.4f, density: 0.8f, life: 30);
            c.Particle(x - Math.Cos(angle) * 0.5, y - Math.Sin(angle) * 0.5, -Math.Cos(angle) * 0.3, -Math.Sin(angle) * 0.3, life: 0.6f, color: 0xc8b48c, size: 2);
        }
        if (_trails.Count > units.Length)
        {
            var alive = units.ToHashSet();
            foreach (var gone in _trails.Keys.Where(entity => !alive.Contains(entity)).ToArray())
                _trails.Remove(gone);
        }
    }
}
