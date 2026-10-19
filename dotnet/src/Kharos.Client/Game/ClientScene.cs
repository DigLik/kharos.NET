using Kharos.Audio;
using Kharos.Audio.Graph;
using Kharos.Client.Looks;
using Kharos.Client.Ui;
using Kharos.Engine.Numerics;
using Kharos.Engine.Terrain;
using Kharos.Gpu;
using Kharos.Mod.Abi;
using Gui = Kharos.Client.Ui.Gui;

namespace Kharos.Client.Game;

/// <summary>Частица: место и скорость в тайлах, возраст и срок, цвет (0xRRGGBB или 0xAARRGGBB), размер в текселях.</summary>
public record struct Particle(float X, float Y, float Vx, float Vy, float Age, float Life, uint Color, float Size, bool Drift);

/// <summary>След на земле: отрезок и ширина в тайлах, цвет 0..1, непрозрачность, плотность, зерно, возраст и срок.</summary>
public record struct Decal(float X1, float Y1, float X2, float Y2, float Width, float R, float G, float B, float Opacity, float Density, float Seed, float Age, float Life);

/// <summary>Камера: центр в тайлах и пикселей на тайл.</summary>
public record struct CameraState(double X, double Y, double Zoom);

/// <summary>Атмосфера кадра: свет 0..1, ветер в тайлах в секунду, осадки 0..1.</summary>
public record struct AtmosphereState(float Light, float WindX, float WindY, float Precipitation);

/// <summary>
/// Свободный спрайт облика (запись Sprite, ABI 2): вид, вариант, фаза, направление, точка привязки в тайлах, масштаб,
/// прозрачность, сторона (0 своя … 3 ничья), слой (LookLayer), rgba-множитель цвета.
/// </summary>
public readonly record struct SceneSprite(int Proto, int Variant, int Frame, float Angle, float X, float Y, float Scale, float Alpha, byte Team, byte Layer, uint Tint);

/// <summary>
/// Уточнение спрайта сущности на кадр (запись Paint, ABI 2): Mask — какие поля заданы (ClientAbi.Paint*); Hide — не
/// рисовать.
/// </summary>
public readonly record struct ScenePaint(byte Mask, int Variant, int Frame, float Built, float Dim)
{
    public bool Has(byte bit) => (Mask & bit) != 0;
    public bool Hide => Has(ClientAbi.PaintHide);

    /// <summary>Поздняя запись поверх ранней: заданные в ней поля побеждают.</summary>
    public ScenePaint Merge(ScenePaint later) => new(
        (byte)(Mask | later.Mask),
        later.Has(ClientAbi.PaintVariant) ? later.Variant : Variant,
        later.Has(ClientAbi.PaintFrame) ? later.Frame : Frame,
        later.Has(ClientAbi.PaintBuilt) ? later.Built : Built,
        later.Has(ClientAbi.PaintDim) ? later.Dim : Dim);
}

/// <summary>Огонь (запись Light, ABI 2): место в тайлах, радиусы пятна и ореола в текселях, яркость.</summary>
public readonly record struct SceneLight(float X, float Y, float Spot, float Bloom, float Level);

/// <summary>Луч (запись Beam, ABI 2): как Frame.Beams — начало, длина, яркость, cos, sin, ближняя полуширина, расширение.</summary>
public readonly record struct SceneBeam(float X, float Y, float Length, float Level, float Cos, float Sin, float Near, float Spread);

/// <summary>
/// Запись выхода кадра для обвязки тестов (Viewer, <see cref="ClientScene.Recorder"/>): рисунок 0x20–0x22 (Values —
/// координаты и толщина, Id — цвет) или звук 0x40–0x43 (Name — имя звука или вид голоса, Id — номер голоса, Values —
/// числа записи по порядку).
/// </summary>
public readonly record struct FrameRecord(byte Op, string? Name, uint Id, float[] Values);

/// <summary>
/// Куда ложится выход кадра клиентских модов (ClientAbi «Выход кадра»): интерфейс, слой поверх мира, частицы, следы,
/// приказы, камера, атмосфера, палитра. Звук — у <see cref="ModSounds"/> своего мода.
/// </summary>
public sealed class ClientScene(Gui gui)
{
    /// <summary>Больше частиц и следов разом не бывает (как в script.rs).</summary>
    public const int MaxParticles = 20_000, MaxDecals = 12_000;

    /// <summary>Больше свободных спрайтов, огней и лучей за кадр не берётся (ABI 2; лишнее отбрасывается).</summary>
    public const int MaxSprites = 20_000, MaxLights = 4_096;

    /// <summary>Палитра по умолчанию — TEAMS из unitArt.ts: свои синие, чужие красные, союзники зелёные; ничьи — как свои, серым.</summary>
    public static readonly uint[] DefaultTeams = [0x1f63d8, 0x5aa9ff, 0xe4f4ff, 0xb3261e, 0xff6b5a, 0xffe9e4, 0x1f7a3c, 0x4ec973, 0xe8fff0, 0x1f63d8, 0x5aa9ff, 0xe4f4ff];

    public Gui Gui { get; } = gui;

    /// <summary>Облик вида по номеру — для картинок интерфейса.</summary>
    public Func<int, Look?> LookOf { get; set; } = _ => null;

    /// <summary>Слой поверх мира, в тайлах.</summary>
    public Batch World { get; set; } = new();

    public CameraState Camera { get; set; } = new(0, 0, 32);
    public AtmosphereState Atmosphere { get; set; } = new(1, 0, 0, 0);

    /// <summary>Палитра сторон: 12 цветов RGB 0..1 × 3 (как Frame.Palette).</summary>
    public float[] Palette { get; } = new float[Frame.Teams * 3 * 3];

    public List<Particle> Particles { get; } = [];
    public LinkedList<Decal> Decals { get; } = new();

    /// <summary>Приказы хосту за кадр.</summary>
    public List<(string Name, byte[] Payload)> Orders { get; } = [];

    /// <summary>Секунды кадра (зерно следов).</summary>
    public double Time { get; set; }

    /// <summary>Сбои разбора за кадр.</summary>
    public List<string> Errors { get; } = [];

    /// <summary>
    /// Запись линий, кругов, рамок и звуковых команд модов по мере разбора (обвязка тестов Viewer); null — не писать.
    /// Свободные спрайты, Paint, огни и лучи и так лежат кадр в <see cref="Sprites"/>, <see cref="Paints"/>,
    /// <see cref="Lights"/>, <see cref="Beams"/>.
    /// </summary>
    public Action<FrameRecord>? Recorder { get; set; }

    // ABI 2 — потребители заполняют пакеты волны 1; до них записи разбираются и лежат здесь кадр.

    /// <summary>Свободные спрайты кадра (Sprite). TODO(Д1): рисовать в ClientGame.Sprites.</summary>
    public List<SceneSprite> Sprites { get; } = [];

    /// <summary>Уточнения спрайтов сущностей кадра (Paint), слитые по сущности. TODO(Д1): применять в ClientGame.Sprites.</summary>
    public Dictionary<uint, ScenePaint> Paints { get; } = [];

    /// <summary>Огни кадра (Light). TODO(Д1): в Frame.Spots и Frame.Blooms (ClientGame.Effects).</summary>
    public List<SceneLight> Lights { get; } = [];

    /// <summary>Лучи кадра (Beam). TODO(Д1): в Frame.Beams (ClientGame.Effects).</summary>
    public List<SceneBeam> Beams { get; } = [];

    /// <summary>Записи настроек мода (Prefs), ещё не сохранённые: ключ мода и байты (пусто — стереть).</summary>
    public List<(string Key, byte[] Value)> Prefs { get; } = [];

    /// <summary>Просьбы к оболочке (Shell): действие ClientAbi.ShellAction* и текст. Разбирает оболочка (Д4).</summary>
    public List<(byte Action, string Arg)> Shell { get; } = [];

    /// <summary>Начало кадра: записи прошлого кадра — прочь.</summary>
    public void BeginFrame()
    {
        Orders.Clear();
        Errors.Clear();
        Sprites.Clear();
        Paints.Clear();
        Lights.Clear();
        Beams.Clear();
        Prefs.Clear();
        Shell.Clear();
    }

    public void SetPalette(ReadOnlySpan<uint> colors)
    {
        for (var i = 0; i < 12; i++)
        {
            Palette[i * 3] = (colors[i] >> 16 & 0xff) / 255f;
            Palette[i * 3 + 1] = (colors[i] >> 8 & 0xff) / 255f;
            Palette[i * 3 + 2] = (colors[i] & 0xff) / 255f;
        }
    }

    public void AddDecal(float x1, float y1, float x2, float y2, float width, float r, float g, float b, float opacity, float density, float life)
    {
        if (opacity <= 0 || life <= 0)
            return;
        var seed = (float)(TerrainGenerator.Hash(JsMath.ToInt32(x1 * 16.0), JsMath.ToInt32(y1 * 16.0), JsMath.ToInt32(Time + Decals.Count)) * 1000);
        if (Decals.Count >= MaxDecals)
            Decals.RemoveFirst();
        Decals.AddLast(new Decal(x1, y1, x2, y2, width, r, g, b, opacity, density, seed, 0, life));
    }
}

/// <summary>
/// Голоса одного клиентского мода: его номера (handle) → звучания <see cref="AudioEngine"/>. Без звука (тесты, нет
/// устройства) команды разбираются и пропадают.
/// </summary>
public sealed class ModSounds(AudioEngine? engine)
{
    private readonly Dictionary<uint, Sound> _sounds = [];
    private readonly List<uint> _ended = [];

    public AudioEngine? Engine { get; } = engine;

    /// <summary>Сколько голосов мода звучит.</summary>
    public int Count => _sounds.Count;

    /// <summary>Отзвучавшие с прошлого раза голоса — для входа кадра.</summary>
    public List<uint> TakeEnded()
    {
        _ended.Clear();
        foreach (var (handle, sound) in _sounds)
            if (sound.IsFinished)
                _ended.Add(handle);
        foreach (var handle in _ended)
            _sounds.Remove(handle);
        return _ended;
    }

    public Sound? this[uint handle] => _sounds.GetValueOrDefault(handle);

    /// <summary>Голос по графу мода: соединения с голосами мода (voice ≠ 0 — номер мода) — на звучания клиента.</summary>
    public void Start(uint handle, VoiceGraph? graph, string? kind, VoiceOptions options)
    {
        if (Engine is null)
            return;
        Sound? sound;
        if (graph is not null)
        {
            if (graph.Connections.Any(c => !c.To.IsLocal))
            {
                var connections = new List<Connection>(graph.Connections.Count);
                foreach (var connection in graph.Connections)
                {
                    if (connection.To.IsLocal)
                        connections.Add(connection);
                    else if (_sounds.TryGetValue(connection.To.Voice, out var target))
                        connections.Add(connection with { To = connection.To with { Voice = target.Id } });
                }
                graph = new VoiceGraph(graph.Nodes, connections, graph.Automations);
            }
            sound = Engine.Voice(graph, options);
        }
        else
            sound = Engine.Voice(kind!, options);
        if (sound is not null)
            _sounds[handle] = sound;
    }

    /// <summary>Узел голоса мода как точка into; нет голоса — null.</summary>
    public Endpoint? Into(uint handle, ushort node) => handle != 0 && _sounds.TryGetValue(handle, out var sound) ? sound.Node(node) : null;

    /// <summary>Отпускает все голоса (мод выключен или пересоздан).</summary>
    public void ReleaseAll()
    {
        foreach (var sound in _sounds.Values)
            sound.Release(0.1f);
        _sounds.Clear();
    }
}
