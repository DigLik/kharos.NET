using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Камера стратегии — перенос cameras/rts.luau: клавиши и края экрана двигают, средняя кнопка тянет, колесо меняет
/// масштаб вокруг курсора. Масштабы — кратные пикселю спрайта, чтобы пиксель-арт не плыл.
/// </summary>
internal sealed class RtsCamera(BaseState state)
{
    /// <summary>Логических пикселей на тайл: 16 — пиксель спрайта в пиксель экрана.</summary>
    private static readonly double[] Zooms = [8, 16, 24, 32, 48, 64];

    private const int Start = 3;

    /// <summary>Скорость клавиш и краёв — экранов в секунду.</summary>
    private const double Pan = 0.9;

    /// <summary>Ширина края экрана, где мышь двигает камеру, в пикселях.</summary>
    private const float Edge = 6;

    /// <summary>Сколько первых кадров ждать своё здание, если снимок пришёл без него.</summary>
    private const int Wait = 60;

    private int _level = Start;
    private (float X, float Y)? _drag;
    private bool _started, _centered;
    private int _frames;

    /// <summary>
    /// Первый кадр: масштаб и камера — туда, где игрок начинает: камера из его счёта (Player.camera: x, y, zoom f64,
    /// её ставит редактор), иначе первое своё здание, иначе свои юниты (в начале партии это MCV с отрядом).
    /// </summary>
    private void Begin(ClientContext c)
    {
        var camera = c.Camera;
        if (!_started)
        {
            camera.Zoom = Zooms[_level];
            _started = true;
        }
        if (_centered || ++_frames > Wait)
            return;
        if (state.Account(c) is { } account && account.Bytes(state.P.Player["camera"]) is { Length: >= 24 } saved)
        {
            (camera.X, camera.Y) = (BitConverter.ToDouble(saved, 0), BitConverter.ToDouble(saved, 8));
            var zoom = BitConverter.ToDouble(saved, 16);
            _level = Array.IndexOf(Zooms, Zooms.MinBy(level => Math.Abs(level - zoom)));
            camera.Zoom = Zooms[_level];
            _centered = true;
            return;
        }
        foreach (var entity in c.Each(state.P.Building, state.P.Owner))
            if (c.Mine(entity) && state.Center(c, entity) is { } middle)
            {
                (camera.X, camera.Y) = (middle.X, middle.Y + 3);
                _centered = true;
                return;
            }
        double sumX = 0, sumY = 0;
        var count = 0;
        foreach (var entity in c.Each(state.P.Unit, state.P.Owner))
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
        // Тянуть средней кнопкой.
        if (input.Pressed(PointerButton.Middle) && input.Inside)
            _drag = (input.X, input.Y);
        if (!input.Down(PointerButton.Middle))
            _drag = null;
        if (_drag is { } drag)
        {
            camera.X -= (input.X - drag.X) / camera.Zoom;
            camera.Y -= (input.Y - drag.Y) / camera.Zoom;
            _drag = (input.X, input.Y);
        }
        // Клавиши и края.
        double dx = 0, dy = 0;
        var keys = input.Keys;
        if (keys.Contains("a") || keys.Contains("left")) dx -= 1;
        if (keys.Contains("d") || keys.Contains("right")) dx += 1;
        if (keys.Contains("w") || keys.Contains("up")) dy -= 1;
        if (keys.Contains("s") || keys.Contains("down")) dy += 1;
        if (input.Inside && input.Focused && _drag is null)
        {
            if (input.X < Edge) dx -= 1;
            if (input.X > c.ScreenWidth - Edge) dx += 1;
            if (input.Y < Edge) dy -= 1;
            if (input.Y > c.ScreenHeight - Edge) dy += 1;
        }
        var speed = Pan * Math.Max(c.ScreenWidth, c.ScreenHeight) / camera.Zoom * dt;
        camera.X += dx * speed;
        camera.Y += dy * speed;
        // Колесо: масштаб вокруг точки под курсором.
        if (input.Wheel != 0 && input.Inside && !input.OverUi)
        {
            var before = camera.Zoom;
            _level = Math.Clamp(_level + (input.Wheel > 0 ? 1 : -1), 0, Zooms.Length - 1);
            camera.Zoom = Zooms[_level];
            if (camera.Zoom != before)
            {
                double px = input.X - c.ScreenWidth / 2.0, py = input.Y - c.ScreenHeight / 2.0;
                camera.X += px / before - px / camera.Zoom;
                camera.Y += py / before - py / camera.Zoom;
            }
        }
        // Не дальше края поля.
        var size = state.Data.FieldSize(c.Game) + 8;
        camera.X = Math.Clamp(camera.X, -size, size);
        camera.Y = Math.Clamp(camera.Y, -size, size);
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
