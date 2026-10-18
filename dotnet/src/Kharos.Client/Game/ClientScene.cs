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
/// Куда ложится выход кадра клиентских модов (ClientAbi «Выход кадра»): интерфейс, слой поверх мира, частицы, следы,
/// приказы, камера, атмосфера, палитра. Звук — у <see cref="ModSounds"/> своего мода.
/// </summary>
public sealed class ClientScene(Gui gui)
{
    /// <summary>Больше частиц и следов разом не бывает (как в script.rs).</summary>
    public const int MaxParticles = 20_000, MaxDecals = 12_000;

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
