using System.Diagnostics;
using System.Runtime.InteropServices;
using Kharos.Audio;
using Kharos.Client.Game;
using Kharos.Client.Net;
using Kharos.Content;
using Kharos.Gpu;
using Kharos.Host;
using Kharos.Protocol;
using Silk.NET.Input;
using Silk.NET.Input.Glfw;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Glfw;
using HostGame = Kharos.Host.Host;

namespace Kharos.Desktop;

/// <summary>
/// Оболочка десктопа (Р10): окно Silk.NET без графического API, поверхность WebGPU, одиночная партия — хост в своём
/// потоке плюс клиент через транспорт в памяти. Кадр: ввод → <see cref="ClientGame.Step"/> → Upload → Render.
/// Масштаб: физический размер — кадровый буфер, логический — он, делённый на масштаб экрана (DPI), он же идёт в
/// <c>View.Scale</c>. KHAROS_SHOT вместо игры снимает кадр в PNG с невидимым окном и выходит.
/// </summary>
public static class Shell
{
    private const double ShotFrameSeconds = 1.0 / 60;

    public static int Run(DesktopOptions options)
    {
        // Сервер — вместо одиночной партии (NetGame.cs); соединение открывается до окна: отказ не мигнёт пустым окном.
        WebSocketTransport? net = null;
        if (options.Server is not null && (net = NetGame.Connect(options)) is null)
            return 1;
        try
        {
            return RunWindow(options, net);
        }
        finally
        {
            net?.Dispose();
        }
    }

    private static int RunWindow(DesktopOptions options, WebSocketTransport? net)
    {
        // Платформы окон и ввода регистрируются явно: поиск по сборкам через отражение не годится для NativeAOT.
        GlfwWindowing.RegisterPlatform();
        GlfwInput.RegisterPlatform();

        // Свои моды; на сервере они не обязательны — недостающие придут с сервера (GameConnector).
        List<ModPackage> packages = net is null ? [.. options.ModRoots.SelectMany(ModPackage.Discover)] : NetGame.LocalMods(options);
        var shot = options.Shot;
        var windowOptions = WindowOptions.Default with
        {
            Title = "Kharos",
            Size = shot is null ? new Vector2D<int>(1280, 800) : new Vector2D<int>(shot.Size.Width, shot.Size.Height),
            API = GraphicsAPI.None,
            VSync = true,
            IsVisible = shot is null,
            WindowBorder = WindowBorder.Resizable,
        };
        using var window = Window.Create(windowOptions);
        window.Initialize();
        var input = new InputMap();
        using var context = window.CreateInput();
        input.Attach(context);
        window.FocusChanged += focused => input.Focused = focused;

        using var gpu = GpuContext.CreateForWindow(window, vsync: true);
        DesktopLog.Write($"видеокарта: {gpu.AdapterName}, {gpu.Backend}");
        using var renderer = new Renderer(gpu);

        AudioEngine? audio = null;
        OpenAlOutput? output = null;
        if (options.Audio)
        {
            audio = new AudioEngine();
            output = OpenAlOutput.TryOpen(audio, out var audioError);
            if (output is null)
            {
                DesktopLog.Write($"звука не будет: {audioError}");
                audio = null;
            }
        }

        LocalGame? local = null;
        ClientGame? game = null;
        try
        {
            var clientOptions = new ClientGameOptions { Audio = audio, Log = DesktopLog.Write };
            if (net is null)
            {
                var content = GameContent.Load(packages);
                local = LocalGame.Start(() => new HostGame(content, new HostOptions { Game = options.Game, Log = DesktopLog.Write }));
                game = new ClientGame(content, local.Client, clientOptions);
            }
            else
            {
                game = new ClientGame(NetGame.Enter(options, net, packages), clientOptions);
            }
            return Loop(options, window, input, gpu, renderer, local, net, game);
        }
        catch (ClientException error) when (net is not null)
        {
            // Сервер не пустил (пароль, версия) или оборвал вход: причина — в Close-кадре.
            DesktopLog.Write($"вход на сервер не удался: {error.Message}; {NetGame.Describe(net)}");
            return 1;
        }
        finally
        {
            // Клиент закрывается первым, потом хост: клиент ещё читает транспорт.
            game?.Dispose();
            local?.Dispose();
            output?.Dispose();
        }
    }

    private static int Loop(DesktopOptions options, IWindow window, InputMap input, GpuContext gpu, Renderer renderer, LocalGame? local, WebSocketTransport? net, ClientGame game)
    {
        var clock = Stopwatch.StartNew();
        var shot = options.Shot;
        var frames = 0;
        while (!window.IsClosing)
        {
            window.DoEvents();
            if (local?.Failure is { } failure)
            {
                DesktopLog.Write($"хост остановился: {failure}");
                return 1;
            }
            // Закрыт — и всё пришедшее уже разобрано кадром; переподключение решит будущее меню.
            if (net is { Closed: true })
            {
                DesktopLog.Write(NetGame.Describe(net));
                return 1;
            }
            if (input.ToggleFullscreen)
            {
                input.ToggleFullscreen = false;
                window.WindowState = window.WindowState == WindowState.Fullscreen ? WindowState.Normal : WindowState.Fullscreen;
            }

            var buffer = window.FramebufferSize;
            var size = window.Size;
            if (buffer.X <= 0 || buffer.Y <= 0 || size.X <= 0 || size.Y <= 0)
            {
                // Окно свёрнуто: рисовать некуда.
                Thread.Sleep(50);
                continue;
            }
            // Окно → физические: кадровый буфер (на Retina он вдвое больше окна). Физические → логические: масштаб экрана.
            var toPhysical = (float)buffer.X / size.X;
            var scale = options.Scale ?? (shot is not null ? 1 : Math.Max(toPhysical > 1.001f ? toPhysical : Dpi.Of(window), 1));
            var toLogical = toPhysical / scale;
            if (shot?.Pointer is { } at)
                input.PlacePointer(at.X / toLogical, at.Y / toLogical);

            renderer.Resize(buffer.X, buffer.Y);
            var frame = game.Step(input.Take(clock.Elapsed.TotalSeconds, buffer.X / scale, buffer.Y / scale, scale, toLogical));
            game.Upload(renderer);
            renderer.Render(frame);
            frames++;

            if (shot is not null)
            {
                if (frames >= shot.Frames)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(shot.Path) ?? ".");
                    renderer.SavePng(shot.Path);
                    ReportShot(game, local?.Ticks ?? 0, frames, buffer);
                    return 0;
                }
                // Хост идёт по часам, а не по кадрам: без паузы 60 кадров пролетели бы за миг игры.
                var wait = frames * ShotFrameSeconds - clock.Elapsed.TotalSeconds;
                if (wait > 0)
                    Thread.Sleep(TimeSpan.FromSeconds(wait));
            }
        }
        return 0;
    }

    private static void ReportShot(ClientGame game, long ticks, int frames, Vector2D<int> buffer)
    {
        DesktopLog.Write($"снимок: {buffer.X}x{buffer.Y}, кадров {frames}, тиков {ticks}, спрайтов {game.Stats.Sprites}, ошибок клиента {game.Errors.Count}");
        foreach (var error in game.Errors.Take(5))
            DesktopLog.Write($"ошибка клиента: {error}");
    }
}

/// <summary>Масштаб экрана окна: на Windows — DPI окна к 96, иначе 1.</summary>
internal static partial class Dpi
{
    public static float Of(IWindow window)
    {
        if (!OperatingSystem.IsWindows() || window.Native?.Win32 is not { } win32)
            return 1;
        var dpi = GetDpiForWindow(win32.Hwnd);
        return dpi > 0 ? dpi / 96f : 1;
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);
}
