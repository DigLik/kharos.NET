using Base.Shared.Orders;
using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;
using MemoryPack;

namespace Base.Client;

/// <summary>
/// Что выбрано в панели редактора (editorPanel.ts): инструмент, игрок, кисть, вид, месторождение, выделенное. Каждая
/// правка — приказ edit.* хосту (право редактора проверяет хост): так одна панель правит и мир слота, и живой мир сервера.
/// </summary>
internal sealed class EditorModel(BaseState state)
{
    /// <summary>Флаг общей памяти мода: редактор открыт (bool). Его читают другие разделы, чтобы молчать: управление, Esc в меню.</summary>
    public const string OpenKey = "editor.open";

    /// <summary>Инструменты base по порядку (TOOLS); кисти модов идут за ними как «brush:&lt;номер&gt;».</summary>
    public static readonly string[] Tools = ["select", "paint", "paving", "building", "unit", "deposit"];

    /// <summary>Сторона месторождения (DEPOSIT_SIZE).</summary>
    public const int DepositSize = 2;

    public BaseState State { get; } = state;
    public PlaceWorld World { get; } = new(state);

    public string Tool { get; set; } = "select";

    /// <summary>Игрок панели: его здания и юниты ставятся, его камера и кредиты правятся; 0 — ничьё.</summary>
    public uint Player { get; set; }

    /// <summary>Выбран ли игрок панели (до первого кадра — свой).</summary>
    public bool PlayerChosen { get; set; }

    public Brush Brush { get; set; } = new() { Terrain = 2, Shape = "square" };
    public int Size { get; set; } = 3;

    /// <summary>Покрытие кисти «Покрытие»: road, foundation; null — снять.</summary>
    public string? Pave { get; set; } = "road";

    public string Building { get; set; } = "generator";
    public string Unit { get; set; } = "tank";
    public string Deposit { get; set; } = "metal";
    public double Reserve { get; set; } = 2500;

    /// <summary>Варианты кистей модов: номер кисти → выбранный ключ.</summary>
    public Dictionary<string, string?> Options { get; } = new(StringComparer.Ordinal);

    /// <summary>Выбранные сущности и месторождения (левые верхние тайлы).</summary>
    public List<uint> Selection { get; } = [];
    public List<(int X, int Y)> Spots { get; } = [];

    /// <summary>Тайл под указателем; нет — null.</summary>
    public (int X, int Y)? Hover { get; set; }

    /// <summary>Ответ последней правки (отказ хоста) или пусто.</summary>
    public string Status { get; set; } = "";

    /// <summary>Призрак юнита под указателем не встанет (красный).</summary>
    public bool GhostBlocked { get; set; }

    /// <summary>Кисти и виды модов (data/editor.json).</summary>
    public EditorExtras Extras { get; private set; } = new();
    private bool _extrasLoaded;

    /// <summary>Текст полей ввода, пока их правят: номер поля → набранное.</summary>
    public Dictionary<string, string> Typed { get; } = new(StringComparer.Ordinal);

    public void Prepare(ClientContext c)
    {
        if (!_extrasLoaded)
        {
            Extras = EditorExtras.Load(c);
            _extrasLoaded = true;
        }
        if (!PlayerChosen)
        {
            Player = c.Player;
            PlayerChosen = true;
        }
    }

    /// <summary>Кисть мода по инструменту «brush:&lt;номер&gt;»; не кисть мода — null.</summary>
    public EditorBrush? ModBrush => Tool.StartsWith("brush:", StringComparison.Ordinal) ? Extras.Brushes.Find(brush => "brush:" + brush.Id == Tool) : null;

    /// <summary>Виды зданий для списка: data/buildings.json по порядку, дописанные модами, без спрятанных.</summary>
    public List<string> BuildingKinds(ClientContext c) =>
        [.. World.Tables(c).Buildings.Keys.Concat(Extras.Buildings).Distinct().Where(kind => !Extras.Hidden.Contains(kind))];

    /// <summary>Виды юнитов для списка.</summary>
    public List<string> UnitKinds(ClientContext c) =>
        [.. World.Tables(c).Units.Keys.Concat(Extras.Units).Distinct().Where(kind => !Extras.Hidden.Contains(kind))];

    /// <summary>Виды месторождений по порядку data/deposits.json (номер вида в слое — место в списке) и их полный запас.</summary>
    public List<(string Kind, double Max)> DepositKinds(ClientContext c)
    {
        if (_deposits is not null)
            return _deposits;
        _deposits = [];
        if (c.TryData("deposits", out var bytes))
        {
            using var json = System.Text.Json.JsonDocument.Parse(bytes.ToArray());
            if (json.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                foreach (var kind in json.RootElement.EnumerateObject())
                    _deposits.Add((kind.Name, kind.Value.ValueKind == System.Text.Json.JsonValueKind.Object && kind.Value.TryGetProperty("max", out var max) && max.TryGetDouble(out var value) ? value : 1000));
        }
        return _deposits;
    }
    private List<(string Kind, double Max)>? _deposits;

    /// <summary>
    /// Игроки партии (playersOf): ростер протокола и владельцы того, что видно в мире (счёт чужого игрока до клиента не
    /// доходит), и заведённые кнопкой «+»; номера по возрастанию.
    /// </summary>
    public List<RosterEntry> Players(ClientContext c)
    {
        if (_roster is null || _rosterRevision != c.RosterRevision || c.Time - _rosterAt > 0.5)
        {
            var known = c.Roster().Where(entry => entry.Player != 0).ToDictionary(entry => entry.Player);
            var owner = State.P.Owner["player"];
            foreach (var entity in c.Each(State.P.Owner))
                if (c.Get(entity, State.P.Owner) is { } values && (uint)values.Number(owner) is var player and not 0)
                    known.TryAdd(player, new RosterEntry(player, "", false, false));
            foreach (var player in _added)
                known.TryAdd(player, new RosterEntry(player, "", false, false));
            _roster = [.. known.Values.OrderBy(entry => entry.Player)];
            (_rosterRevision, _rosterAt) = (c.RosterRevision, c.Time);
        }
        return _roster;
    }
    private List<RosterEntry>? _roster;
    private uint _rosterRevision;
    private double _rosterAt;
    private readonly List<uint> _added = [];

    /// <summary>Новый игрок (edit.addPlayer): номер — следующий за известными; он в списке, пока его не узнает ростер.</summary>
    public uint AddPlayer(ClientContext c)
    {
        var players = Players(c);
        var next = (players.Count == 0 ? 0 : players.Max(entry => entry.Player)) + 1;
        Send(c, EditAddPlayerOrder.Name, new EditAddPlayerOrder());
        _added.Add(next);
        _roster = null;
        return next;
    }

    /// <summary>Подпись игрока: номер и ник.</summary>
    public static string Label(RosterEntry entry) => entry.Nick.Length > 0 ? $"{entry.Player} {entry.Nick}" : entry.Player.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ мир

    /// <summary>Месторождение, что накрывает точку мира: левый верхний тайл, вид (номер) и запас; нет — null.</summary>
    public (int X, int Y, int Kind, double Reserve)? DepositUnder(ClientContext c, double x, double y)
    {
        if (World.DepositsLayer(c) is not { } layer)
            return null;
        foreach (var item in Kharos.Mod.Sdk.Tools.Items(layer, 0, x, y, DepositSize + 1))
            if (x >= item.X && x < item.X + DepositSize && y >= item.Y && y < item.Y + DepositSize)
                return ((int)item.X, (int)item.Y, item.Fields.Length > 0 ? (int)item.Fields[0] : 0, item.Fields.Length > 1 ? item.Fields[1] : 0);
        return null;
    }

    /// <summary>Месторождения, левый верхний тайл которых в прямоугольнике.</summary>
    public IEnumerable<(int X, int Y)> DepositsIn(ClientContext c, double left, double top, double right, double bottom)
    {
        if (World.DepositsLayer(c) is not { } layer)
            yield break;
        var (cx, cy) = ((left + right) / 2, (top + bottom) / 2);
        var radius = Math.Sqrt((right - left) * (right - left) + (bottom - top) * (bottom - top)) / 2 + DepositSize;
        foreach (var item in Kharos.Mod.Sdk.Tools.Items(layer, 0, cx, cy, radius))
            if (item.X + DepositSize > left && item.X < right && item.Y + DepositSize > top && item.Y < bottom)
                yield return ((int)item.X, (int)item.Y);
    }

    /// <summary>Юнит или здание под точкой (entityAt): турели и прочее — не выбираются.</summary>
    public uint? EntityAt(ClientContext c, double x, double y) =>
        c.Pick(x, y) is { } entity && (c.Has(entity, State.P.Unit) || c.Has(entity, State.P.Building)) ? entity : null;

    /// <summary>Может ли юнит вида type стоять на тайле (как CanPut у хоста): летающему — на карте, наземному — проходимо.</summary>
    public bool CanStand(ClientContext c, string type, int x, int y)
    {
        if (World.Tables(c).Units.GetValueOrDefault(type) is not { } spec)
            return false;
        if (spec.Kind == "air")
            return (Kharos.Mod.Sdk.Tools.Tile(x, y) & TileFlags.OnMap) != 0;
        return Kharos.Mod.Sdk.Tools.Walkable(x, y, spec.Kind == "infantry" ? PathFlags.Foot : PathFlags.None);
    }

    /// <summary>Где ляжет месторождение под указателем на тайле (x, y): серединой под ним, как здание.</summary>
    public static (int X, int Y) DepositSpot(int x, int y) => (x - DepositSize / 2, y - DepositSize / 2);

    /// <summary>Тайлы квадратной кисти размера size с серединой (x, y): x, y подряд.</summary>
    public static int[] Square(int x, int y, int size)
    {
        var from = size / 2;
        var tiles = new int[size * size * 2];
        var at = 0;
        for (var ty = y - from; ty < y - from + size; ty++)
            for (var tx = x - from; tx < x - from + size; tx++)
            {
                tiles[at++] = tx;
                tiles[at++] = ty;
            }
        return tiles;
    }

    public static int[] Flat(IEnumerable<(int X, int Y)> spots) => [.. spots.SelectMany(spot => (int[])[spot.X, spot.Y])];

    // ------------------------------------------------------------------ правки

    /// <summary>Правка хосту: приказ edit.* (или приказ кисти мода) с payload MemoryPack. Ответ — сигналом refused.</summary>
    public void Send<T>(ClientContext c, string order, T payload) where T : class
    {
        Status = "";
        c.Send(order, MemoryPackSerializer.Serialize(payload));
    }

    /// <summary>Сносит выбранное: сущности и месторождения.</summary>
    public void EraseSelection(ClientContext c)
    {
        if (Selection.Count > 0)
            Send(c, EditEraseOrder.Name, new EditEraseOrder { Entities = [.. Selection] });
        if (Spots.Count > 0)
            Send(c, EditRemoveDepositOrder.Name, new EditRemoveDepositOrder { Spots = Flat(Spots) });
        ClearPicks();
    }

    public void ClearPicks()
    {
        Selection.Clear();
        Spots.Clear();
        Typed.Clear();
    }

    /// <summary>Выбранное, что ещё живо.</summary>
    public void Prune(ClientContext c) => Selection.RemoveAll(entity => !c.Alive(entity));
}
