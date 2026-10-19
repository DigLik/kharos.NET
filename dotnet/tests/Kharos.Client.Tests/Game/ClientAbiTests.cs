using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Kharos.Client.Game;
using Kharos.Client.Looks;
using Kharos.Client.Ui;
using Kharos.Content;
using Kharos.Engine.World;
using Kharos.Host;
using Kharos.Protocol;
using HostGame = Kharos.Host.Host;
using S = Kharos.Mod.Sdk.Client;

namespace Kharos.Client.Tests.Game;

/// <summary>
/// ABI клиента, версия 2 (docs/dotnet-n6.md, Н6.2): записи и инструменты новой версии — запись SDK мода, разбор движком;
/// вход кадра (язык, правки полей, режим оболочки, ревизия ростера); поток облика с вариантами и кадрами работы.
/// </summary>
public sealed class ClientAbiTests : IDisposable
{
    private readonly Fonts _fonts = Fonts.Load();
    private readonly (Look[] Looks, LookAtlas Atlas) _looks = Demo.Atlas();

    public void Dispose() => _fonts.Dispose();

    /// <summary>Вход кадра движка и выход мода по телу body; выход разобран в сцену.</summary>
    private (ClientScene Scene, Gui Gui) Frame(ClientInput clientInput, uint roster, Action<S.ClientContext> body)
    {
        var gui = new Gui(_fonts);
        var scene = new ClientScene(gui) { LookOf = proto => proto is >= 1 and <= 3 ? _looks.Looks[proto - 1] : null };
        var writer = new AbiWriter();
        gui.Begin(clientInput.ToUi());
        new FrameInput().Write(writer, new FrameClock(1, 1 / 60.0, 3, 0, 0.05, 1), clientInput, scene.Camera, gui, scene.Atmosphere, [], null, [], roster);
        var output = new S.ClientContext().Frame(writer.Written, body);
        ClientStream.Apply(output, scene);
        gui.Finish();
        return (scene, gui);
    }

    [Fact]
    public void VersionIsTwoOnBothSides()
    {
        Assert.Equal(2, Kharos.Mod.Sdk.Runtime.ClientRuntime.AbiVersion());
        // Мод хоста — прежней версии: версии сторон независимы.
        Assert.Equal(1, Kharos.Mod.Sdk.ModSdkInfo.AbiVersion);
        Assert.Equal(2, Kharos.Mod.Sdk.ModSdkInfo.ClientAbiVersion);
    }

    [Fact]
    public void ShellPartOfFrameInputReachesTheMod()
    {
        var input = new ClientInput { Width = 800, Height = 600, Lang = "en", Edits = [("name", "Ann"), ("seed", "42"), ("name", "Анна")], Shell = 1 | 2 };
        var seen = "";
        Frame(input, 7, c => seen = $"{c.Lang} {c.Ui.Edited("name")} {c.Ui.Edited("seed")} {c.Ui.Edited("none") ?? "-"} {c.Shell} {c.RosterRevision}");
        // Правки одного поля за кадр — побеждает последняя.
        Assert.Equal("en Анна 42 - Menu, Editor 7", seen);
    }

    [Fact]
    public void NewRecordsRoundTrip()
    {
        var tint = S.Draw.Rgba(0x80ff0000);
        var (scene, gui) = Frame(new ClientInput { Width = 800, Height = 600 }, 0, c =>
        {
            c.Ui.Box(new S.Style { Id = "panel", Anchor = new S.Anchor(Left: 0, Top: 0), Direction = S.Direction.Row }, () =>
            {
                c.Ui.Field("name", "Ann", new S.Style { Width = 120, Height = 20 });
                c.Ui.Icon(3, 1, 12);
                c.Ui.Icon(42, 0, 10);
            });
            c.Draw.Sprite(2, 3.5, 4.25, angle: 1.5, variant: 1, frame: 2, scale: 2, alpha: 0.5f, team: 1, layer: S.LookLayer.Air, tint: 0x80ff0000);
            c.Draw.Paint(5, variant: 3, built: 0.5f);
            c.Draw.Paint(5, frame: 2, dim: 0.25f, hide: true);
            c.Draw.Paint(6);
            c.Draw.Light(1.5, 2.5, 32, 6, 0.75f);
            c.Draw.Beam(1, 2, 0, length: 50, level: 0.7f, near: 3, spread: 0.2f);
            c.Prefs.Set("steps", [1, 2, 3]);
            c.Prefs.Remove("old");
            c.Ask(S.ShellAction.Chat, "/give");
            c.Ask(S.ShellAction.Menu);
        });
        Assert.Empty(scene.Errors);

        // Field — узел с номером поля, Icon — картинки (облик есть) и место (облика нет).
        var field = Assert.IsType<Rect>(gui.RectOf("name"));
        Assert.Equal((120f, 20f), (field.W, field.H));
        Assert.NotNull(gui.RectOf("panel"));

        var sprite = Assert.Single(scene.Sprites);
        Assert.Equal(new SceneSprite(2, 1, 2, 1.5f, 3.5f, 4.25f, 2, 0.5f, 1, 5, tint), sprite);

        Assert.Equal(2, scene.Paints.Count);
        var paint = scene.Paints[5];
        Assert.Equal(new ScenePaint(1 | 2 | 4 | 8 | 16, 3, 2, 0.5f, 0.25f), paint);
        Assert.True(paint.Hide);
        Assert.Equal(new ScenePaint(0, 0, 0, 1, 0), scene.Paints[6]);
        Assert.False(scene.Paints[6].Hide);

        Assert.Equal([new SceneLight(1.5f, 2.5f, 32, 6, 0.75f)], scene.Lights);
        Assert.Equal([new SceneBeam(1, 2, 50, 0.7f, 1, 0, 3, 0.2f)], scene.Beams);

        Assert.Equal(2, scene.Prefs.Count);
        Assert.Equal("steps", scene.Prefs[0].Key);
        Assert.Equal([1, 2, 3], scene.Prefs[0].Value);
        Assert.Equal(("old", 0), (scene.Prefs[1].Key, scene.Prefs[1].Value.Length));
        Assert.Equal([((byte)2, "/give"), ((byte)1, "")], scene.Shell);

        scene.BeginFrame();
        Assert.Empty(scene.Sprites);
        Assert.Empty(scene.Paints);
        Assert.Empty(scene.Shell);
    }

    [Fact]
    public void TruncatedNewRecordIsAFormatError()
    {
        var scene = new ClientScene(new Gui(_fonts));
        foreach (var op in new byte[] { 0x1A, 0x1B, 0x23, 0x24, 0x25, 0x26, 0x51, 0x52 })
            Assert.Throws<FormatException>(() => ClientStream.Apply([op, 1], scene));
    }

    [Fact]
    public void LookStreamCarriesVariantsAndWorkingFrames()
    {
        static uint Color(int variant, int frame, int direction) => 0x100000u * (uint)(variant + 1) + 0x1000u * (uint)(frame + 1) + (uint)(direction + 1);
        var spec = new S.LookSpec((_, _, _) => throw new InvalidOperationException("рисует DrawCell"))
        {
            Width = 8, Height = 8, Origin = (0, 0), Directions = 4, Phases = 2, Variants = 3, Working = true, Shadow = null,
            DrawCell = (canvas, variant, angle, phase) => canvas.Pixel(1.5, 1.5, Color(variant, phase, (int)Math.Round(angle / S.Canvas.Turn * 4) % 4)),
        };
        var bytes = spec.Encode();
        Assert.Equal(64 | 128, bytes[4] & (64 | 128));
        var look = Look.Build(1, LookStream.Decode(bytes));
        Assert.Equal((3, 4, 2, 4), (look.Variants, look.Frames, look.Phases, look.Directions));
        Assert.Equal(3 * 4 * 4, look.Images.Count);
        for (var variant = 0; variant < 3; variant++)
            for (var phase = 0; phase < 2; phase++)
                foreach (var working in new[] { false, true })
                    for (var direction = 0; direction < 4; direction++)
                    {
                        var image = look.Images[look.Cell(direction / 4.0 * Look.Turn, phase, variant, working)];
                        var at = (1 * 8 + 1) * 4;
                        var color = Color(variant, phase + (working ? 2 : 0), direction);
                        Assert.Equal(((byte)(color >> 16), (byte)(color >> 8), (byte)color, (byte)255), (image[at], image[at + 1], image[at + 2], image[at + 3]));
                    }
        // Прежняя ячейка (фаза, направление) — вариант 0 в покое.
        Assert.Equal(look.Cell(Look.Turn / 4, 1, 0), look.Cell(Look.Turn / 4, 1));

        // Облик без вариантов и работы — поток прежний, без новых флагов и полей.
        var plain = new S.LookSpec((canvas, _, _) => canvas.Pixel(0, 0, 0xffffff)) { Width = 4, Height = 4, Phases = 2 };
        Assert.Equal(0, plain.Encode()[4] & (64 | 128));
        var old = Look.Build(1, LookStream.Decode(plain.Encode()));
        Assert.Equal((1, 2, 2), (old.Variants, old.Frames, old.Images.Count));
    }

    [Fact]
    public void ManifestCarriesCellLooks()
    {
        var builder = new S.ClientBuilder();
        builder.CellLook("paving", 0, true, "", "paving/road", "paving/foundation");
        var manifest = RoundTrip(builder);
        Assert.Equal(2, manifest.Abi);
        var cell = Assert.Single(manifest.CellLooks);
        Assert.Equal(("paving", "0", true), (cell.Layer, cell.Field, cell.Connect));
        Assert.Equal(["", "paving/road", "paving/foundation"], cell.Looks);
    }

    /// <summary>Манифест SDK → MemoryPack → манифест движка (как kh_load).</summary>
    private static Kharos.Mod.Abi.ModManifest RoundTrip(S.ClientBuilder builder)
    {
        var sdk = typeof(S.ClientBuilder).GetMethod("ToManifest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(builder, null)!;
        var serialize = typeof(MemoryPack.MemoryPackSerializer).GetMethods()
            .Single(m => m.Name == "Serialize" && m.IsGenericMethodDefinition && m.ReturnType == typeof(byte[]) && m.GetParameters().Length == 2);
        var bytes = (byte[])serialize.MakeGenericMethod(sdk.GetType()).Invoke(null, [sdk, null])!;
        return MemoryPack.MemoryPackSerializer.Deserialize<Kharos.Mod.Abi.ModManifest>(bytes)!;
    }

    [Fact]
    public void HelloClientLoadsOnVersionTwo()
    {
        using var session = new HelloSession();
        var mod = Assert.Single(session.Game.Mods);
        Assert.Equal(2, mod.Module.LoadInfo.Abi);
        Assert.Equal(2, mod.Manifest.Abi);
        Assert.Empty(mod.Manifest.CellLooks);
    }
}

/// <summary>
/// Инструменты ABI 2 (48 Text, 49 Prefs, 50 Roster, 51 Settings) через API мода (SDK → kh.tool → ClientTools движка) и
/// общие 14 Children, 16 Tiles, 17 Occupant, 21 Walkable над репликой — на них стоят Placing и canPlace.
/// </summary>
public sealed class ClientAbiToolsTests : IDisposable
{
    private const int ToolChildren = 14, ToolTiles = 16, ToolOccupant = 17, ToolWalkable = 21;
    private const byte TileOnMap = 1, TilePassable = 2, TileBuilding = 16, TileUnit = 64;

    private readonly Fonts _fonts = Fonts.Load();
    private readonly HostGame _host;
    private readonly HostSession _session;
    private readonly MemoryTransport _end;
    private readonly Replica _replica = new();
    private readonly ClientTools _tools;
    private readonly MemoryStorage _storage = new();
    private readonly List<string> _log = [];

    public ClientAbiToolsTests()
    {
        var lab = new ModPackage("lab", Encoding.UTF8.GetBytes("""{ "name": "lab", "game": "lab" }"""), new Dictionary<string, byte[]>
        {
            ["games"] = Encoding.UTF8.GetBytes("""{ "lab": { "size": 64, "fog": false, "land": "flat" } }"""),
            ["lands"] = Encoding.UTF8.GetBytes("""{ "flat": { "seed": 3, "blank": true } }"""),
        });
        var content = GameContent.Load([lab]);
        _host = new HostGame(content, new HostOptions { Seed = 1 });
        _host.Start();
        _session = new HostSession(_host, new HostSessionOptions { SendRate = 0 });
        (var hostEnd, _end) = MemoryTransport.Pair();
        _session.Connect(hostEnd, 1);
        _replica.Pump(_end);
        _tools = new ClientTools(_replica, content, () => _replica.Terrain, new Poses(), _fonts)
        {
            Log = _log.Add,
            Storage = _storage,
            Mod = "lab",
            Players = () => [new PlayerInfo(1, "Ann", true), new PlayerInfo(2, "Bob", false)],
            Admin = () => true,
        };
        Kharos.Mod.Sdk.Runtime.ClrBridge.Attach(() => 0.5, (_, message) => _log.Add(message), Tool);
    }

    public void Dispose()
    {
        Kharos.Mod.Sdk.Runtime.ClrBridge.Detach();
        _end.Dispose();
        _host.Dispose();
        _fonts.Dispose();
    }

    private unsafe int Tool(int id, nint input, int inputLength, nint output, int capacity) =>
        _tools.Call(id, new ReadOnlySpan<byte>((void*)input, inputLength), new Span<byte>((void*)output, capacity));

    private void Tick()
    {
        _session.Step();
        _replica.Pump(_end);
    }

    private byte[] Call(int id, params object[] values)
    {
        var input = new List<byte>();
        foreach (var value in values)
            input.AddRange(value switch
            {
                uint u => BitConverter.GetBytes(u),
                int i => BitConverter.GetBytes(i),
                _ => throw new ArgumentException(value.GetType().Name),
            });
        var output = new byte[4096];
        var length = _tools.Call(id, [.. input], output);
        Assert.True(length >= 0, $"инструмент {id}: код {length}; {string.Join(" | ", _log)}");
        return output[..length];
    }

    private static uint[] U32s(byte[] bytes) => [.. Enumerable.Range(0, bytes.Length / 4).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4)))];

    [Fact]
    public void TextResolvesTheKeyInThePlayersLanguageFromTheMergedDictionaries()
    {
        var mod = new S.ClientContext();
        // Без словарей инструмент отдаёт сам ключ.
        Assert.Equal("units.count", mod.Text("units.count", "n", "3"));
        _tools.Texts = Kharos.I18n.Texts.Shell();
        _tools.Texts.Add("ru", """{ "units": { "count": "{n} шт. ({what:lower})" } }"""u8);
        _tools.Texts.Add("en", """{ "units": { "count": "{n} pcs ({what:lower})" } }"""u8);
        Assert.Equal("3 шт. (ёж)", mod.Text("units.count", "n", "3", "what", "ЁЖ"));
        _tools.Lang = "en";
        Assert.Equal("3 pcs (ёж)", mod.Text("units.count", "n", "3", "what", "ЁЖ"));
        Assert.Equal("Play", mod.Text("common.play"));
        Assert.Equal("{n} pcs ({what:lower})", mod.Text("units.count"));
        Assert.Equal("no.such.key", mod.Text("no.such.key"));
    }

    [Fact]
    public void PrefsAreReadThroughStorageAndWrittenByRecord()
    {
        _storage.Set(ClientTools.PrefsKey("lab", "volume"), [7, 8]);
        _storage.Set(ClientTools.PrefsKey("other", "volume"), [9]);
        var mod = new S.ClientContext();
        Assert.Equal([7, 8], mod.Prefs.Get("volume"));
        Assert.Empty(mod.Prefs.Get("missing"));
        mod.Prefs.Set("steps", "мост");
        Assert.Equal("мост", mod.Prefs.GetString("steps"));
        // Чужой мод не видит настроек lab.
        _tools.Mod = "other";
        Assert.Equal([9], new S.ClientContext().Prefs.Get("volume"));
        _tools.Mod = null;
        Assert.Empty(new S.ClientContext().Prefs.Get("volume"));
    }

    [Fact]
    public void LookToolNamesFreeLooks()
    {
        // Без партии облика нет; с ней — номер от движка (облик вида или свободный, после всех видов).
        Assert.Equal(-1, new S.ClientContext().LookId("goods/metal"));
        _tools.LookId = name => name switch { "goods/metal" => 1000, "units/buggy" => 4, _ => -1 };
        var mod = new S.ClientContext();
        Assert.Equal(1000, mod.LookId("goods/metal"));
        Assert.Equal(4, mod.LookId("units/buggy"));
        Assert.Equal(-1, mod.LookId("goods/none"));
    }

    [Fact]
    public void RosterComesFromProtocolPlayers()
    {
        var roster = new S.ClientContext().Roster();
        Assert.Equal(1u, _replica.Player);
        // Администратор — только свой игрок: чужой режим протокол не шлёт.
        Assert.Equal([new S.RosterEntry(1, "Ann", true, true), new S.RosterEntry(2, "Bob", false, false)], roster);
    }

    [Fact]
    public void SettingsAreGameEntryMapAndRights()
    {
        using var json = JsonDocument.Parse(new S.ClientContext().Settings());
        var root = json.RootElement;
        Assert.Equal("lab", root.GetProperty("game").GetString());
        Assert.Equal(64, root.GetProperty("settings").GetProperty("size").GetInt32());
        Assert.Equal("flat", root.GetProperty("settings").GetProperty("land").GetString());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("map").ValueKind);
        Assert.True(root.GetProperty("map").GetProperty("right").GetInt32() > root.GetProperty("map").GetProperty("left").GetInt32());
        Assert.False(root.GetProperty("fog").GetBoolean());
        Assert.True(root.GetProperty("rights").GetProperty("admin").GetBoolean());
        Assert.False(root.GetProperty("rights").GetProperty("editor").GetBoolean());
    }

    [Fact]
    public void PlacingToolsWorkOnTheReplica()
    {
        var world = _host.World;
        var building = world.Spawn();
        world.Add(building, Builtin.Position);
        world.Set(building, Builtin.Position, "x", FieldValue.Of(10));
        world.Set(building, Builtin.Position, "y", FieldValue.Of(12));
        world.Add(building, Builtin.Footprint);
        world.Set(building, Builtin.Footprint, "w", FieldValue.Of(2));
        world.Set(building, Builtin.Footprint, "h", FieldValue.Of(2));
        world.Set(building, Builtin.Footprint, "solid", FieldValue.Of(true));
        var turret = world.Spawn();
        world.Add(turret, Builtin.Position);
        world.Add(turret, Builtin.Mount);
        world.Set(turret, Builtin.Mount, "parent", FieldValue.Of(building));
        var unit = world.Spawn();
        world.Add(unit, Builtin.Position);
        world.Set(unit, Builtin.Position, "x", FieldValue.Of(20.5));
        world.Set(unit, Builtin.Position, "y", FieldValue.Of(20.5));
        world.Add(unit, Builtin.Body);
        Tick();
        Assert.True(_replica.World!.Alive(building) && _replica.World.Alive(unit), "сущности не дошли до реплики");

        // 17 Occupant: здание — на своих четырёх тайлах.
        Assert.Equal([building, building, 0u, 0u], U32s(Call(ToolOccupant, 4u, 10, 12, 11, 13, 12, 12, 9, 12)));
        // 21 Walkable: на здании не стоят, рядом — да.
        Assert.Equal([0, 1], Call(ToolWalkable, 0u, 2u, 11, 12, 5, 5));
        // 16 Tiles: биты здания, карты, проходимости и юнита.
        var tiles = Call(ToolTiles, 10, 12, 1u, 1u);
        Assert.Equal(TileOnMap | TileBuilding, tiles[0] & (TileOnMap | TileBuilding));
        var open = Call(ToolTiles, 20, 20, 1u, 1u);
        Assert.Equal(TileOnMap | TilePassable | TileUnit, open[0] & (TileOnMap | TilePassable | TileUnit));
        Assert.Equal(0, Call(ToolTiles, -1000, -1000, 1u, 1u)[0] & TileOnMap);
        // 14 Children: турель на здании.
        Assert.Equal([1u, turret], U32s(Call(ToolChildren, 1u, building)));

        // Здание ушло — тайлы свободны (занятость ведётся по реплике).
        world.Destroy(building);
        Tick();
        Assert.Equal([0u], U32s(Call(ToolOccupant, 1u, 10, 12)));
        Assert.Equal([1], Call(ToolWalkable, 0u, 1u, 11, 12));
        Assert.Empty(_log);
    }
}
