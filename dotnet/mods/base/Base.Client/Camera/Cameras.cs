using System.Text.Json;
using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Камера стратегии — перенос camera.ts + cameraMotion.ts + кусок controls.ts: WASD и стрелки разгоняют камеру
/// (Shift — втрое быстрее), отпущенные — она тормозит и встаёт; средняя кнопка тянет, а брошенная камера катится дальше;
/// колесо задаёт масштаб, который камера догоняет плавно, держа на месте точку под курсором; полёт к точке — разгон
/// и торможение без перелёта (<see cref="CameraShake.FlyTo"/>), прыжок по мини-карте — сразу (<see cref="CameraShake.Jump"/>).
/// Указатель у края экрана двигает камеру, только пока тянут рамку выделения. В партии она начинается там, где игрок:
/// у его сохранённой камеры, главного здания или отряда. Движение — чистая <see cref="CameraMotion"/>, здесь только ввод.
/// </summary>
internal sealed class RtsCamera
{
    /// <summary>Скорость клавиш, пикселей экрана в секунду, и во сколько раз быстрее с Shift.</summary>
    private const double KeySpeed = 900, KeyBoost = 3;

    /// <summary>Ширина края экрана, где указатель двигает камеру, пока тянут рамку, в пикселях.</summary>
    private const float Edge = 6;

    /// <summary>Масштаб колеса за строку прокрутки: показатель экспоненты (одно деление колеса — около 16 %).</summary>
    private const double WheelStep = 0.15;

    /// <summary>Масштаб в начале партии.</summary>
    private const double StartZoom = 32;

    /// <summary>Сколько первых кадров искать своё место, если снимок пришёл без него (хост ещё не прислал мир).</summary>
    private const int Wait = 300;

    /// <summary>
    /// Окно местности — 512 тайлов, чанк — 32 и запас по краям 2×2 (landWindow.ts): экран с запасом должен в него
    /// помещаться, поэтому мельче этого масштаб не бывает.
    /// </summary>
    private const double WindowTiles = 512 - 32 - 4;

    private readonly BaseState _state;
    private readonly CameraModel _model = new();
    private readonly CameraMotion _motion;
    private (float X, float Y)? _drag;
    private bool _started, _centered;
    private int _frames;
    private (double Left, double Top, double Right, double Bottom)? _bounds;

    public RtsCamera(BaseState state)
    {
        _state = state;
        _motion = new CameraMotion(_model);
    }

    /// <summary>
    /// Границы карты в тайлах: из приветствия хоста (Settings.map), нет — по размеру поля партии с запасом.
    /// </summary>
    private (double Left, double Top, double Right, double Bottom) Bounds(ClientContext c)
    {
        if (_bounds is { } known)
            return known;
        var size = _state.Data.FieldSize(c.Game) + 8;
        var bounds = (Left: -size, Top: -size, Right: size, Bottom: size);
        try
        {
            using var document = JsonDocument.Parse(c.Settings());
            if (document.RootElement.TryGetProperty("map", out var map) && map.ValueKind == JsonValueKind.Object)
                bounds = (map.GetProperty("left").GetDouble(), map.GetProperty("top").GetDouble(), map.GetProperty("right").GetDouble(), map.GetProperty("bottom").GetDouble());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Приветствия нет или оно без границ — остаются по размеру поля.
        }
        _bounds = bounds;
        return bounds;
    }

    /// <summary>
    /// Первые кадры: масштаб и камера — туда, где игрок начинает: камера из его счёта (Player.camera: x, y, zoom f64,
    /// её ставит редактор), иначе первое своё здание, иначе свои юниты (в начале партии это MCV с отрядом). Мир приходит
    /// не с первым кадром, поэтому ищет, пока не найдёт (но не дольше <see cref="Wait"/> кадров).
    /// </summary>
    private void Begin(ClientContext c)
    {
        var camera = c.Camera;
        if (!_started)
        {
            camera.Zoom = StartZoom;
            _started = true;
        }
        if (_centered || ++_frames > Wait)
            return;
        if (_state.Account(c) is { } account && account.Bytes(_state.P.Player["camera"]) is { Length: >= 24 } saved)
        {
            (camera.X, camera.Y) = (BitConverter.ToDouble(saved, 0), BitConverter.ToDouble(saved, 8));
            var zoom = BitConverter.ToDouble(saved, 16);
            if (zoom >= CameraModel.MinZoom && zoom <= CameraModel.MaxZoom)
                camera.Zoom = zoom;
            _centered = true;
            return;
        }
        foreach (var entity in c.Each(_state.P.Building, _state.P.Owner))
            if (c.Mine(entity) && _state.Center(c, entity) is { } middle)
            {
                (camera.X, camera.Y) = (middle.X, middle.Y + 3);
                _centered = true;
                return;
            }
        double sumX = 0, sumY = 0;
        var count = 0;
        foreach (var entity in c.Each(_state.P.Unit, _state.P.Owner))
            if (c.Mine(entity) && c.Pose(entity) is { } at)
            {
                sumX += at.X;
                sumY += at.Y;
                count++;
            }
        if (count > 0)
        {
            (camera.X, camera.Y) = (sumX / count, sumY / count + 3);
            _centered = true;
        }
    }

    public void Update(ClientContext c)
    {
        Begin(c);
        var input = c.Input;
        var camera = c.Camera;
        var dt = c.Dt;
        var menu = (c.Shell & ShellMode.Menu) != 0;

        _model.Width = c.ScreenWidth;
        _model.Height = c.ScreenHeight;
        _model.LowZoom = Math.Max(c.ScreenWidth, c.ScreenHeight) / WindowTiles;
        _model.X = camera.X;
        _model.Y = camera.Y;
        _model.Zoom = Math.Max(camera.Zoom, _model.ClampZoom(0));
        _motion.Simple = (c.Shell & ShellMode.Editor) != 0;

        // Просьбы других разделов: прыжок по мини-карте и полёт к группе.
        if (c.Shared.Remove(CameraShake.JumpKey, out var jump) && jump is ValueTuple<double, double> to)
            _motion.Jump(to.Item1, to.Item2);
        if (c.Shared.Remove(CameraShake.FlyKey, out var fly) && fly is ValueTuple<double, double> goal)
            _motion.FlyTo(goal.Item1, goal.Item2);

        // Тянуть средней кнопкой; отпущенная посреди движения — катится дальше.
        if (input.Pressed(PointerButton.Middle) && input.Inside)
            _drag = (input.X, input.Y);
        if (!input.Down(PointerButton.Middle) && _drag is not null)
        {
            _motion.Release();
            _drag = null;
        }
        if (_drag is { } drag && input.Inside)
        {
            _motion.Drag(input.X - drag.X, input.Y - drag.Y);
            _drag = (input.X, input.Y);
        }

        // Клавиши и края: меню поверх партии камеру не двигает.
        double dx = 0, dy = 0;
        if (!menu)
        {
            var keys = input.Keys;
            if (keys.Contains("a") || keys.Contains("left")) dx -= 1;
            if (keys.Contains("d") || keys.Contains("right")) dx += 1;
            if (keys.Contains("w") || keys.Contains("up")) dy -= 1;
            if (keys.Contains("s") || keys.Contains("down")) dy += 1;
            // Указатель у края двигает камеру только пока тянут рамку выделения: она растёт за край.
            if (input.Inside && input.Focused && c.Shared.ContainsKey(SelectControl.BoxKey))
            {
                if (input.X < Edge) dx -= 1;
                if (input.X > c.ScreenWidth - Edge) dx += 1;
                if (input.Y < Edge) dy -= 1;
                if (input.Y > c.ScreenHeight - Edge) dy += 1;
            }
            // Колесо: масштаб вокруг точки под курсором.
            if (input.Wheel != 0 && input.Inside && !input.OverUi)
                _motion.ZoomBy(Math.Exp(input.Wheel * WheelStep), input.X, input.Y);
        }
        var speed = KeySpeed * (input.Shift ? KeyBoost : 1);
        _motion.Update(dt, Math.Sign(dx) * speed, Math.Sign(dy) * speed);

        // Середина экрана не уходит за границы карты.
        var bounds = Bounds(c);
        var focus = _model.Focus;
        _model.CenterOn(Math.Clamp(focus.X, bounds.Left, bounds.Right), Math.Clamp(focus.Y, bounds.Top, bounds.Bottom));
        _motion.Measure(dt);
        c.Shared[CameraShake.SpeedKey] = _motion.Speed;

        camera.X = _model.X;
        camera.Y = _model.Y;
        camera.Zoom = _model.Zoom;
    }
}

/// <summary>
/// Камера слежения — перенос cameras/follow.luau: держит в центре сущность из <see cref="Shared.FollowKey"/> (или первую
/// выделенную) и мягко догоняет её; колесо — масштаб. Так смотрит игра от персонажа. Skirmish её не берёт.
/// </summary>
internal sealed class FollowCamera
{
    private static readonly double[] Zooms = [16, 24, 32, 48, 64];

    private const int Start = 2;

    /// <summary>Насколько быстро догоняет: доля расстояния за секунду.</summary>
    private const double Catch = 6;

    private int _level = Start;
    private bool _started;

    public void Update(ClientContext c)
    {
        var camera = c.Camera;
        if (!_started)
        {
            camera.Zoom = Zooms[_level];
            _started = true;
        }
        uint? target = c.Shared.TryGetValue(Shared.FollowKey, out var follow) && follow is uint chosen ? chosen : Shared.Selection(c) is { Length: > 0 } selection ? selection[0] : null;
        if (target is { } entity && c.Alive(entity) && c.Pose(entity) is { } at)
        {
            var k = Math.Min(1, Catch * c.Dt);
            camera.X += (at.X - camera.X) * k;
            camera.Y += (at.Y - camera.Y) * k;
        }
        var input = c.Input;
        if (input.Wheel != 0 && !input.OverUi)
        {
            _level = Math.Clamp(_level + (input.Wheel > 0 ? 1 : -1), 0, Zooms.Length - 1);
            camera.Zoom = Zooms[_level];
        }
    }
}
