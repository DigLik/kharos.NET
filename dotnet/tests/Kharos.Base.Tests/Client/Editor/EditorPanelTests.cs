using System.Text.Json.Nodes;
using Base.Shared;
using Base.Shared.Orders;
using Kharos.Base.Tests.Kernel;
using Kharos.Client.Game;
using Kharos.Client.Shell;
using Kharos.Engine.Terrain;
using Kharos.I18n;
using Kharos.Mod.Sdk.Client;

namespace Kharos.Base.Tests.Client.Editor;

/// <summary>
/// Редактор (Н6-3): право от хоста включает режим оболочки, панель base (Base.Client/Editor) шлёт правки edit.*, призрак
/// юнита — свободный спрайт, мир редактора сохраняется в слот с настройками и разведанным исходного (Shell/Editor*).
/// Клиент — на обвязке Viewer (CLR, реплика сценария).
/// </summary>
public sealed class EditorPanelTests
{
    private static readonly Brush RockBrush = new() { Terrain = (int)TerrainType.Rock, Biome = 0, Tier = 1, Cliff = false, Shape = "square" };

    private static void SkipUnless() => Assert.SkipWhen(Viewer.Unavailable(Sim.DefaultMode) is not null, Viewer.Unavailable(Sim.DefaultMode) ?? "");

    /// <summary>Мир на скале 1-го яруса без тумана, игроки 1 и 2; зритель — игрок 1, камера на (4, 4).</summary>
    private static (Sim Sim, Viewer Viewer) World(bool fog = false)
    {
        SkipUnless();
        var sim = Sim.Create(new SimOptions { Size = 64, Land = Scene.Land, Fog = fog, Players = [1, 2] });
        sim.Paint(0, 0, 40, RockBrush);
        var viewer = sim.Viewer(1);
        viewer.Camera = new CameraState(4, 4, 32);
        viewer.Run(0.1);
        return (sim, viewer);
    }

    /// <summary>
    /// Вход в редактор, как у оболочки (EditorShell): /admin, через полсекунды /editor; право пришло — флаг редактора во
    /// входе кадра (ShellApp.ShellFlags).
    /// </summary>
    private static void Enter(Viewer viewer)
    {
        viewer.Game.Say("/admin");
        Assert.True(viewer.Until(() => viewer.Game.Admin, 5), "нет права администратора");
        Thread.Sleep(TimeSpan.FromSeconds(EditorShell.ChatGap));
        viewer.Game.Say("/editor");
        Assert.True(viewer.Until(() => viewer.Game.Editor, 5), "нет права редактора");
        viewer.Input = viewer.Input with { Shell = viewer.Game.Editor ? (byte)ShellMode.Editor : (byte)0 };
        viewer.Step(3);
    }

    /// <summary>Указатель над точкой мира, кадры.</summary>
    private static void Hover(Viewer viewer, double x, double y)
    {
        viewer.Input = viewer.Input with { Pointer = viewer.Last.View.ToScreen((float)x, (float)y) };
        viewer.Step(2);
    }

    [Fact]
    public void EditorRightOpensThePanel()
    {
        var (sim, viewer) = World();
        using var _ = sim;
        viewer.Step(2);
        Assert.False(viewer.Ui.Has("editor"));
        Enter(viewer);
        Assert.True(viewer.Ui.Has("editor"));
        Assert.True(viewer.Ui.Has("editor:tool:paint"));
        Assert.Contains(Hud.RequestButtonsTests.T("editor.tools.paving"), viewer.Ui.Texts());
        // Флаг снят — панели нет (вышли из редактора).
        viewer.Input = viewer.Input with { Shell = 0 };
        viewer.Step(2);
        Assert.False(viewer.Ui.Has("editor"));
        Assert.Empty(viewer.Log);
    }

    [Fact]
    public void BrushSendsPaintAndPavingEdits()
    {
        var (sim, viewer) = World();
        using var _ = sim;
        Enter(viewer);
        viewer.Click("editor:tool:paint");
        viewer.Click("editor:terrain:0");
        viewer.ClickWorld(4.5, 4.5);
        var paint = Assert.Single(viewer.Sent.Of<EditPaintOrder>());
        Assert.Equal((4, 4, 3), (paint.X, paint.Y, paint.Size));
        Assert.Equal((int)TerrainType.Swamp, paint.Brush.Terrain);
        viewer.Run(0.2);
        Assert.Equal(TerrainType.Swamp, sim.Land!.TerrainAt(4, 4));
        Assert.Equal(TerrainType.Swamp, sim.Land.TerrainAt(5, 5));
        Assert.Equal(TerrainType.Rock, sim.Land.TerrainAt(6, 6));

        // Покрытие — квадрат кисти дороги игроку панели (свой).
        viewer.Click("editor:tool:paving");
        viewer.ClickWorld(8.5, 2.5);
        var pave = Assert.Single(viewer.Sent.Of<EditPaveOrder>());
        Assert.Equal("road", pave.Kind);
        Assert.Equal(1u, pave.Player);
        Assert.Equal(18, pave.Tiles.Length);
        Assert.Contains((8, 2), Enumerable.Range(0, 9).Select(i => (pave.Tiles[2 * i], pave.Tiles[2 * i + 1])));
        viewer.Run(0.2);
        // Готовым сразу, без строителей и денег.
        Assert.True(sim.IsPaved("road", 8, 2));
        Assert.Equal(1u, sim.PaveOwnerAt(9, 3));
        Assert.Equal(0u, sim.PaveAt(8, 2));
        Assert.Empty(viewer.Log);
    }

    [Fact]
    public void OwnerChangeGoesToTheHost()
    {
        var (sim, viewer) = World();
        using var _ = sim;
        var tank = sim.PutUnit("tank", 1, 6.5, 6.5);
        // Игрок 2 в партии, но не в сети: панель узнаёт его по тому, чем он владеет.
        sim.PutBuilding("generator", 12, 0, 2);
        viewer.Run(0.1);
        Enter(viewer);
        viewer.ClickWorld(6.5, 6.5);
        viewer.Step();
        Assert.True(viewer.Ui.Has("editor:owner:2"), string.Join(", ", viewer.Ui.Ids()));
        viewer.Click("editor:owner:2");
        var owner = Assert.Single(viewer.Sent.Of<EditOwnerOrder>());
        Assert.Equal([tank], owner.Entities);
        Assert.Equal(2u, owner.Player);
        viewer.Run(0.2);
        Assert.Equal(2u, sim.OwnerOf(tank));
        Assert.Empty(viewer.Log);
    }

    [Fact(DisplayName = "призрак юнита не выбирается, краснеет там, где не встать, и не в счёт мира")]
    public void GhostIsNotInTheWorld()
    {
        var (sim, viewer) = World();
        using var _ = sim;
        sim.PutBuilding("generator", 0, 0, 1);
        viewer.Run(0.1);
        Enter(viewer);
        viewer.Click("editor:tool:unit");
        viewer.Click("editor:item:tank");
        Hover(viewer, 10.5, 10.5);
        var ghost = Assert.Single(viewer.Draws.Sprites);
        Assert.Equal((10.5f, 10.5f), (ghost.X, ghost.Y));
        Assert.Equal(Draw.Rgba(0xffffff), ghost.Tint);
        // Призрак — не сущность: ни в мире хоста, ни в реплике его нет, выбрать нечего.
        Assert.Equal(0, sim.Count(Names.Unit.Name));
        var replicaUnits = new List<uint>();
        viewer.World.Query([viewer.World.Schema.Id(Names.Unit.Name)], replicaUnits);
        Assert.Empty(replicaUnits);
        Hover(viewer, 0.5, 0.5);
        ghost = Assert.Single(viewer.Draws.Sprites);
        Assert.NotEqual(Draw.Rgba(0xffffff), ghost.Tint);
        // Инструмент сменили — призрака нет, и в мире по-прежнему ни одного юнита.
        viewer.Click("editor:tool:select");
        Hover(viewer, 10.5, 10.5);
        Assert.Empty(viewer.Draws.Sprites);
        viewer.Run(0.1);
        Assert.Equal(0, sim.Count(Names.Unit.Name));
        Assert.Empty(viewer.Sent.Of<EditUnitOrder>());
        Assert.Empty(viewer.Log);
    }

    /// <summary>Партия редактора у оболочки: правленый мир — сохранением (как EditorSession десктопа).</summary>
    private sealed class EditorSessionStub(Launch launch, ClientGame game, Func<byte[]> save) : IGameSession, IEditorSession
    {
        public Launch Launch { get; } = launch;
        public SessionState State => SessionState.Ready;
        public string? Status => null;
        public string? Error => null;
        public bool WrongPassword => false;
        public ClientGame? Game { get; } = game;
        public void Poll() { }
        public void Retry(string password) { }
        public void Leave() { }
        public void Dispose() { }
        public byte[]? SaveMap() => save();
    }

    [Fact]
    public void EditedMapSavesToTheSlotAndReloads()
    {
        // Исходный мир слота — с туманом: танк игрока 1 разведал округу (0, 0).
        SkipUnless();
        using var original = Sim.Create(new SimOptions { Size = 64, Land = Scene.Land, Fog = true, Players = [1, 2] });
        original.Paint(0, 0, 40, RockBrush);
        original.PutUnit("tank", 1, 0.5, 0.5);
        original.Advance(10);
        Assert.True(original.Explored(1, 0.5, 0.5));
        var originalSave = original.Save();
        var settings = SaveInfo.Read(originalSave).Settings;

        // Редактор поднимает его без тумана: правка кистью из панели.
        Assert.False(JsonNode.Parse(EditorMap.WithoutFog(settings))!["fog"]!.GetValue<bool>());
        var (sim, viewer) = World();
        using var _ = sim;
        Enter(viewer);
        viewer.Click("editor:tool:paint");
        viewer.Click("editor:terrain:0");
        viewer.ClickWorld(20.5, 20.5);
        viewer.Run(0.2);
        Assert.Equal(TerrainType.Swamp, sim.Land!.TerrainAt(20, 20));

        // «Сохранить» оболочки — в слот; настройки и разведанное — исходного.
        var slots = new SaveSlots(new MemoryStorage());
        var slot = slots.Create("карта", 64, 1, settings: settings);
        var shell = new EditorShell(slots);
        var session = new EditorSessionStub(new EditorLaunch(slot), viewer.Game, () => EditorMap.Restore(sim.Save(), settings, originalSave));
        var words = new Words(Texts.Shell(), () => "ru");
        shell.Save(session, (EditorLaunch)session.Launch, words, viewer.Game);
        Assert.Equal(words["editor.savedToSlot"], shell.Status);
        Assert.False(shell.Dirty);

        var stored = slots.Load(slot.Id);
        Assert.NotNull(stored);
        Assert.Equal(settings, SaveInfo.Read(stored).Settings);
        Assert.False(slots.Find(slot.Id)!.Empty);
        using var reloaded = sim.Load(stored);
        Assert.Equal(TerrainType.Swamp, reloaded.Land!.TerrainAt(20, 20));
        Assert.NotNull(reloaded.Host.Vision);
        Assert.True(reloaded.Explored(1, 0.5, 0.5));
        Assert.False(reloaded.Explored(1, 30.5, -30.5));
    }
}
