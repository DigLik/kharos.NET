using System.Globalization;
using Kharos.Client.Game;
using Kharos.Client.Ui;
using Kharos.Gpu;
using Kharos.Mod.Abi;
using RenderFrame = Kharos.Gpu.Frame;

namespace Kharos.Client.Shell;

/// <summary>
/// Фон главного меню (docs/dotnet-n6.md, Н6-4; screens/showcase.ts в TS): партия <c>showcase</c> без тумана идёт за меню, а
/// сценки — добыча, стройка, оборона, бои — сменяют друг друга. Партию заводит та же <see cref="IGameLauncher"/>, что и обычные
/// (десктоп — хост в своём потоке), на каждый слайд — новая крошечная местность со своим зерном, час и погода, имя сценки — в
/// настройках партии (<c>scene</c>). Клиент мода играет в режиме меню (ClientAbi.ShellMenu): камера-режиссёр, музыка, без
/// управления и звуков мира. Слайд идёт <see cref="SlideTime"/> секунд; перед концом и пока слайд собирается экран темнеет
/// (<see cref="Veil"/>), меню рисует оболочка поверх. Дёшево: пока партия начинается, фон <see cref="Suspend"/>ится — партия
/// закрывается вместе с хостом, а к меню фон начинается заново. Не поднялась — повтор через <see cref="RetryTime"/> секунд.
/// </summary>
public sealed class ShowcaseBackground : IMenuBackground
{
    /// <summary>Сколько длится слайд и затемнение между слайдами, секунды.</summary>
    public const double SlideTime = 40, FadeTime = 1.2;

    /// <summary>Пока слайд собирается, экран тёмный, секунды после готовности партии.</summary>
    public const double SettleTime = 0.7;

    /// <summary>Сколько ждать мира, прежде чем сдаться и взять другую сценку, секунды.</summary>
    public const double EmptyTime = 3;

    /// <summary>Через сколько секунд повторять после неудачи.</summary>
    public const double RetryTime = 10;

    /// <summary>Прозрачность затемнения при показе слайда (в TS — 0,25).</summary>
    public const float Shown = 0.25f;

    private readonly IGameLauncher _launcher;
    private readonly Random _random;
    private readonly string[] _scenes;
    private readonly string _game;
    private IGameSession? _session;
    private string? _scene;
    private double? _readyAt;
    private double _retryAt = double.NegativeInfinity;
    private double _changed;
    private float _from = 1, _veil = 1;
    private bool _shown;
    private ulong _slides;

    public ShowcaseBackground(IGameLauncher launcher, Random? random = null, string game = "showcase", IReadOnlyList<string>? scenes = null)
    {
        _launcher = launcher;
        _random = random ?? new Random();
        _game = game;
        _scenes = [.. scenes ?? DefaultScenes];
    }

    public bool Ready { get; private set; }

    public float Veil => _veil;

    /// <summary>Сколько слайдов начато (для тестов).</summary>
    public ulong Slides => _slides;

    public ulong Generation => _slides;

    /// <summary>Идёт ли партия фона (поднимается или показывается).</summary>
    public bool Running => _session is not null;

    /// <summary>Имя сценки текущего слайда; нет — null.</summary>
    public string? Scene => _scene;

    /// <summary>Отладка: показывать только эту сценку (KHAROS_SHOT_SCENE и подобное); null — по очереди.</summary>
    public string? Forced { get; init; }

    /// <summary>Отладка: час начала (сутки стоят); null — случайный час и быстрые сутки.</summary>
    public double? Hour { get; init; }

    /// <summary>Имя сценки витрины (для KHAROS_SHOT_SCENE).</summary>
    public static bool IsScene(string name) => DefaultScenes.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static readonly string[] DefaultScenes = ["mining", "construction", "defense", "battle", "armor", "infantry", "air"];

    private void Show(bool on, double now)
    {
        if (on == _shown)
            return;
        _from = _veil;
        _changed = now;
        _shown = on;
    }

    private string NextScene()
    {
        if (Forced is { } forced)
            return forced;
        var choices = _scenes.Where(name => name != _scene).ToArray();
        return choices.Length == 0 ? _scenes[0] : choices[_random.Next(choices.Length)];
    }

    private string Settings(string scene)
    {
        var hour = Hour ?? (_random.NextDouble() < 0.75 ? 7 + _random.NextDouble() * 11 : _random.NextDouble() * 24);
        var seed = _random.Next(int.MaxValue);
        var inv = CultureInfo.InvariantCulture;
        return "{\"scene\":\"" + scene + "\",\"land\":{\"seed\":" + seed.ToString(inv) + "},\"weather\":{\"startHour\":"
            + hour.ToString("0.###", inv) + (Hour is null ? ",\"dayLength\":600,\"changes\":true}}" : ",\"dayLength\":0}}");
    }

    private void Drop()
    {
        try
        {
            _session?.Dispose();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Фон не должен ронять оболочку.
        }
        _session = null;
        _readyAt = null;
        Ready = false;
    }

    /// <summary>Слайд закончился или не встал: партия закрыта, на следующем кадре начнётся другой.</summary>
    private void Next(double now)
    {
        Drop();
        _veil = _from = 1;
        _shown = false;
        _changed = now;
    }

    public void Suspend()
    {
        Drop();
        _veil = _from = 1;
        _shown = false;
    }

    private static bool Empty(ClientGame game)
    {
        if (game.Replica.World is not { } world)
            return true;
        var found = new List<uint>();
        world.Query([world.Schema.Id("Proto")], found);
        return found.Count == 0;
    }

    public RenderFrame? Step(in ClientInput input)
    {
        var now = input.Time;
        if (_session is null)
        {
            if (now < _retryAt)
                return null;
            _scene = NextScene();
            _slides++;
            _changed = now;
            _session = _launcher.Start(new GameLaunch(_game, Settings(_scene)));
        }
        _session.Poll();
        if (_session.State == SessionState.Failed)
        {
            Drop();
            _retryAt = now + RetryTime;
            return null;
        }
        if (_session.State != SessionState.Ready || _session.Game is not { } game)
            return null;
        _readyAt ??= now;
        var age = now - _readyAt.Value;
        if (age >= SlideTime || (age >= EmptyTime && age < EmptyTime + 1 && Empty(game)))
        {
            Next(now);
            return null;
        }
        if (age >= SettleTime && age < SlideTime - FadeTime)
            Show(true, now);
        else if (age >= SlideTime - FadeTime)
            Show(false, now);
        var share = (float)Math.Min(1, (now - _changed) / FadeTime);
        _veil = _from + ((_shown ? Shown : 1) - _from) * share;

        var gameInput = input with
        {
            Pointer = null, Down = PointerButtons.None, Pressed = PointerButtons.None, Released = PointerButtons.None, Wheel = 0,
            Keys = [], Hits = [], Text = "", Edits = [], Shell = ClientAbi.ShellMenu,
        };
        game.Lang = input.Lang;
        var frame = game.Step(gameInput);
        Ready = true;
        return frame;
    }

    public void Upload(Renderer renderer)
    {
        if (_session?.Game is { } game && Ready)
            game.Upload(renderer);
    }

    public void Dispose() => Drop();
}
