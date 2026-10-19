using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Раздел Camera: камера партии по подаче (client.camera: rts или follow) и тряска от взрывов. Движение — Cameras.cs
/// (<see cref="CameraMotion"/>), тряска — Shake.cs: другие разделы просят её через <see cref="CameraShake.Add"/>, а сдвиг
/// кладётся поверх камеры на кадр и снимается в начале следующего, так что камера под тряской не уползает.
/// TODO(В): Camera/Director.cs (режиссёр витрины меню).
/// </summary>
internal sealed class CameraSection : Section
{
    private RtsCamera _rts = null!;
    private FollowCamera _follow = null!;
    private readonly Shake _shake = new();
    private (double X, double Y) _shift;

    public override void Setup(ClientBuilder client) => client.Camera("base.camera", Frame);

    public override void Load(BaseState state)
    {
        base.Load(state);
        _rts = new RtsCamera(state);
        _follow = new FollowCamera();
    }

    public override void Frame(ClientContext c)
    {
        var camera = c.Camera;
        // Прошлая тряска снимается до того, как камера двинется.
        camera.X -= _shift.X;
        camera.Y -= _shift.Y;
        _shift = (0, 0);
        switch (Profile?.Camera)
        {
            case "rts": _rts.Update(c); break;
            case "follow": _follow.Update(c); break;
            default: return;
        }
        if (c.Shared.Remove(CameraShake.ShakeKey, out var held) && held is double amount)
            _shake.Add(amount);
        _shake.Update(c.Dt);
        _shift = _shake.Offset(camera.Zoom);
        camera.X += _shift.X;
        camera.Y += _shift.Y;
    }
}
