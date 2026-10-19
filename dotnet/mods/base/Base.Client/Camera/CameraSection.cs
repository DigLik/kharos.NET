using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Раздел Camera: камера партии по подаче (client.camera: rts или follow). TODO(К2): Camera/Motion.cs (разгон, инерция,
/// полёт), Camera/Shake.cs (тряска); TODO(В): Camera/Director.cs (режиссёр витрины меню).
/// </summary>
internal sealed class CameraSection : Section
{
    private RtsCamera _rts = null!;
    private FollowCamera _follow = null!;

    public override void Setup(ClientBuilder client) => client.Camera("base.camera", Frame);

    public override void Load(BaseState state)
    {
        base.Load(state);
        _rts = new RtsCamera(state);
        _follow = new FollowCamera();
    }

    public override void Frame(ClientContext c)
    {
        switch (Profile?.Camera)
        {
            case "rts": _rts.Update(c); break;
            case "follow": _follow.Update(c); break;
        }
    }
}
