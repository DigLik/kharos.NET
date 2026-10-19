using System.Buffers.Binary;
using Base.Host.Kernel;
using Base.Host.Sections.Pipes;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Showcase;

/// <summary>
/// Раздел Showcase (docs/dotnet-n6.md, Н6-4): сценки фона меню — партия showcase в data/games.json (поле <c>scene</c>:
/// имя сценки; меню подставляет его настройками партии). Сценка встаёт на первых двух тиках после входа игрока
/// (<see cref="Scene.Create"/>, <see cref="Scene.Settle"/>) и дальше раз в полсекунды ведёт мир (<see cref="Scene.Drive"/>); кончилась
/// — мир стоит как есть, а меню сменит слайд по времени. У других партий раздел молчит.
/// </summary>
internal static class ShowcaseSection
{
    private const int DriveTicks = 10;

    public static void Setup(ModBuilder mod)
    {
        mod.On("player_joined", OnJoined);
        mod.System("showcase/scene", new SceneSystem()).Key("showcase.scene").After("players.sandbox");
    }

    private static uint _player;
    private static Scene? _scene;
    private static int _phase;
    private static long _ticks;

    /// <summary>Есть ли у партии сценка (поле scene в games.json или в настройках).</summary>
    private static bool Active => !string.IsNullOrEmpty(Core.Game.Scene);

    private static void OnJoined(EventContext ctx)
    {
        if (!Active || ctx.Payload.Length < 4 || _scene is not null)
            return;
        _player = BinaryPrimitives.ReadUInt32LittleEndian(ctx.Payload);
        _scene = Scenes.Make(Core.Game.Scene);
        _phase = 0;
        _ticks = 0;
    }

    /// <summary>Система showcase/scene.</summary>
    private sealed class SceneSystem : ISystem
    {
        public void Bind(SystemBinding binding)
        {
        }

        public void Run(SystemContext ctx)
        {
            if (_scene is not { } scene)
                return;
            var call = Call.Of(ctx);
            if (scene.Connecting && !Piping.ConnectAll(call, _player))
                scene.Connecting = false;
            switch (_phase)
            {
                case 0:
                    _phase = scene.Create(call, _player) ? 1 : 3;
                    return;
                case 1:
                    scene.Settle(call, _player);
                    _phase = 2;
                    return;
                case 2:
                    if (++_ticks % DriveTicks == 0 && !scene.Drive(call, _player, DriveTicks * Core.Step))
                        _phase = 3;
                    return;
            }
        }
    }
}
