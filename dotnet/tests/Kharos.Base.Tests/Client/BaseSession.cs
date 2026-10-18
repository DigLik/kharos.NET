using System.Diagnostics;
using Kharos.Client.Game;
using Kharos.Client.Ui;
using Kharos.Content;
using Base.Shared.Orders;
using Kharos.Engine.Layers;
using Kharos.Engine.World;
using Kharos.Host;
using MemoryPack;
using HostGame = Kharos.Host.Host;
using RenderFrame = Kharos.Gpu.Frame;

namespace Kharos.Base.Tests;

/// <summary>
/// Партия base целиком: хост (host.wasm) в своём потоке (LocalGame) и клиент (client.wasm) с транспортом в памяти — как в
/// игре, но без окна и звука. Хост тикает быстрее часов (по умолчанию в десять раз), чтобы производство не ждало секунды.
/// Без собранных host.wasm и client.wasm тесты пропускаются.
/// </summary>
internal sealed class BaseSession : IDisposable
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public const string NotBuilt = "mods/base/host.wasm и client.wasm не собраны: pwsh dotnet/tools/build-mods.ps1 -Mod base";

    public static bool Built => BaseGame.Built && File.Exists(Path.Combine(BaseGame.ModDir, "client.wasm"));

    /// <summary>Папка картинок для просмотра глазами: dotnet/.cache/base.</summary>
    public static string Cache(string name)
    {
        var directory = Path.Combine(BaseGame.DotnetRoot, ".cache", "base");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, name);
    }

    /// <summary>Картинка RGBA в PNG в папке .cache/base.</summary>
    public static void Png(string name, byte[] rgba, int width, int height)
    {
        using var stream = File.Create(Cache(name));
        new StbImageWriteSharp.ImageWriter().WritePng(rgba, width, height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, stream);
    }

    public BaseSession(int width = 1280, int height = 720, TimeSpan? step = null)
    {
        Assert.SkipUnless(Built, NotBuilt);
        Content = BaseGame.Content();
        Local = LocalGame.Start(() => new HostGame(Content, new HostOptions { Modules = new Kharos.Plugins.Game.GameModuleOptions { CallTimeoutMs = 1000 }, Seed = 1 }), step: step ?? TimeSpan.FromMilliseconds(5));
        Game = new ClientGame(Content, Local.Client, new ClientGameOptions { ModuleOptions = new Kharos.Plugins.Game.GameModuleOptions { CallTimeoutMs = 1000 }, Log = Log.Add });
        Input = new ClientInput { Width = width, Height = height };
    }

    public GameContent Content { get; }
    public LocalGame Local { get; }
    public ClientGame Game { get; }
    public List<string> Log { get; } = [];
    public ClientInput Input { get; set; }

    /// <summary>Кадр по часам: ввод — <see cref="Input"/> (или данный) с текущим временем.</summary>
    public RenderFrame Step(ClientInput? input = null)
    {
        var given = (input ?? Input) with { Time = _clock.Elapsed.TotalSeconds };
        return Game.Step(given);
    }

    /// <summary>Кадры по ~60 в секунду, пока until не скажет «да»; не дождались — провал с причиной.</summary>
    public RenderFrame Until(Func<RenderFrame, bool> until, string what, double seconds = 20)
    {
        var deadline = _clock.Elapsed.TotalSeconds + seconds;
        while (true)
        {
            var frame = Step();
            if (until(frame))
                return frame;
            Assert.True(_clock.Elapsed.TotalSeconds < deadline, $"не дождались: {what}; журнал: {string.Join(" | ", Log.TakeLast(5))}; ошибки: {string.Join(" | ", Game.Errors.TakeLast(5))}");
            Thread.Sleep(10);
        }
    }

    /// <summary>Несколько кадров с одним вводом (хост успевает сделать тики).</summary>
    public RenderFrame Steps(int frames, ClientInput? input = null)
    {
        RenderFrame frame = Game.Frame;
        for (var i = 0; i < frames; i++)
        {
            frame = Step(input);
            Thread.Sleep(4);
        }
        return frame;
    }

    public GameWorld World => Game.Replica.World!;

    public uint Player => Game.Replica.Player;

    /// <summary>Сущности с компонентами по именам, по возрастанию номеров.</summary>
    public List<uint> With(params string[] components)
    {
        var list = new List<uint>();
        World.Query([.. components.Select(name => World.Schema.Id(name))], list);
        list.Sort();
        return list;
    }

    /// <summary>Своё: у сущности Owner.player равен игроку клиента.</summary>
    public List<uint> Mine(params string[] components) =>
        [.. With([.. components, "Owner"]).Where(entity => World.Number(entity, Builtin.Owner, 0) == Player)];

    /// <summary>Свои сущности вида path («units/buggy», «buildings/command»), по возрастанию номеров.</summary>
    public List<uint> Of(string path)
    {
        var proto = Content.FindProto(path)!.Id;
        return [.. Mine("Proto").Where(entity => (int)Number(entity, "Proto", "id") == proto)];
    }

    public (double X, double Y) At(uint entity) => (World.Number(entity, Builtin.Position, 0), World.Number(entity, Builtin.Position, 1));

    public double Number(uint entity, string component, string field)
    {
        var id = World.Schema.Id(component);
        return World.Number(entity, id, World.Schema[id].FieldIndex(field));
    }

    /// <summary>Сколько приказов в очереди Shift у юнита (Orders.list).</summary>
    public int Queue(uint entity)
    {
        var id = World.Schema.Id("Orders");
        if (!World.Has(entity, id))
            return 0;
        var bytes = World.GetBytes(entity, id, World.Schema[id].FieldIndex("list")).ToArray();
        return bytes.Length == 0 ? 0 : MemoryPackSerializer.Deserialize<OrderQueue>(bytes)?.Items.Length ?? 0;
    }

    /// <summary>Сколько месторождений в слое deposits у клиента (известные ему).</summary>
    public int DepositCount()
    {
        var layers = Game.Replica.Layers!;
        var id = layers.Find("deposits") ?? throw new Xunit.Sdk.XunitException("нет слоя deposits");
        return layers.Cells(id, LayerOwner.World).Sum(cell => layers.Items(id, LayerOwner.World, cell).Count);
    }

    /// <summary>Режим администратора и редактора карты: дальше приказы edit.* идут с правом редактора (локальной игре пароль не нужен).</summary>
    public void OpenEditor()
    {
        Game.Say("/admin test");
        Game.Say("/editor");
    }

    /// <summary>Кредиты своего игрока по реплике.</summary>
    public double Credits() => With("Player").Where(entity => Number(entity, "Player", "id") == Player).Select(entity => Number(entity, "Player", "credits")).FirstOrDefault();

    /// <summary>Все тексты панели последнего кадра, по порядку обхода.</summary>
    public List<string> Texts()
    {
        var texts = new List<string>();
        void Walk(Node node)
        {
            if (node.Kind == LeafKind.Text)
                texts.Add(node.Text);
            foreach (var child in node.Children)
                Walk(child);
        }
        foreach (var root in Game.Gui.Roots)
            Walk(root);
        return texts;
    }

    /// <summary>Заголовок панели сведений о выбранном (узел info:title); ничего не выбрано — null.</summary>
    public string? InfoTitle()
    {
        static string? Find(Node node)
        {
            if (node.Style.Id == "info:title")
                return node.Children.FirstOrDefault(child => child.Kind == LeafKind.Text)?.Text;
            foreach (var child in node.Children)
                if (Find(child) is { } found)
                    return found;
            return null;
        }
        foreach (var root in Game.Gui.Roots)
            if (Find(root) is { } title)
                return title;
        return null;
    }

    /// <summary>Спрайтов в кадре по слоям.</summary>
    public static int[] Layers(RenderFrame frame) => [.. frame.SpriteLayers.Select(layer => layer.Count / RenderFrame.Sprite)];

    /// <summary>Центр узла по номеру (место прошлого кадра).</summary>
    public (float X, float Y) CenterOf(string id)
    {
        var rect = Game.Gui.RectOf(id) ?? throw new Xunit.Sdk.XunitException($"нет узла {id}");
        return (rect.X + rect.W / 2, rect.Y + rect.H / 2);
    }

    /// <summary>Щелчок мышью: наведение, нажатие, отпускание — три кадра с одним указателем.</summary>
    public void Click(PointerButtons button, (float X, float Y) at, bool shift = false)
    {
        var input = Input with { Pointer = at, Shift = shift };
        Step(input);
        Step(input with { Pressed = button, Down = button });
        Step(input with { Released = button });
        Step(input);
    }

    /// <summary>Рамка левой кнопкой от from до to.</summary>
    public void Drag((float X, float Y) from, (float X, float Y) to)
    {
        Step(Input with { Pointer = from });
        Step(Input with { Pointer = from, Pressed = PointerButtons.Left, Down = PointerButtons.Left });
        Step(Input with { Pointer = to, Down = PointerButtons.Left });
        Step(Input with { Pointer = to, Down = PointerButtons.Left });
        Step(Input with { Pointer = to, Released = PointerButtons.Left });
        Step(Input with { Pointer = to });
    }

    public void Dispose()
    {
        Game.Dispose();
        Local.Dispose();
    }
}
