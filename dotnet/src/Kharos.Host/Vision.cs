using Kharos.Engine.Layers;
using Kharos.Engine.World;
using Kharos.Protocol;

namespace Kharos.Host;

/// <summary>
/// Обзор игроков — система движка <c>engine.vision</c> (vision.ts, crates/sim/src/vision.rs): что игрок видит сейчас и
/// что разведал. Глаз — <c>Sight { radius }</c> у сущности с <c>Owner</c> и <c>Position</c>, не сидящей на носителе
/// (<c>Mount.parent</c>); у здания (<c>Footprint</c>) круг — от середины основания. Вспышки — <c>Reveal { radius,
/// until, all }</c> с <c>Position</c>, пока тик меньше until: all — всем, иначе хозяину. Разведка у союзников
/// (<see cref="Teams"/>) общая: каждый видит то, что видят глаза и вспышки любого из них. Круг — ряды, чья середина в
/// круге; в ряду — тайлы от ⌊x − s⌋ до ⌊x + s⌋, s — полухорда на середине ряда. Пересчёт — раз в <see cref="Ticks"/>
/// тиков. По игроку — тайлы карты построчно: 0 — не разведан, 1 — разведан, 2 — виден сейчас; изменённые переносятся в
/// слои игрока <see cref="Fog.Sight"/> и <see cref="Fog.Explored"/>: так туман сохраняется и уходит клиенту чанками.
/// </summary>
public sealed class Vision
{
    /// <summary>Раз во сколько тиков пересчитывается обзор (на тиках с остатком 1).</summary>
    public const int Ticks = 4;

    private const byte Unseen = 0, Known = 1, Shown = 2, Relit = 3;

    private readonly SortedDictionary<uint, Sight> _sights = [];
    private readonly LayerSet? _layers;
    private readonly int _sight = -1, _explored = -1;

    // Рабочее пересчёта: глаза по группам обзора, вспышки всем, открытое группой, метки тайлов поколениями.
    private readonly Dictionary<long, List<Eye>> _eyes = [];
    private readonly List<Eye> _everyone = [];
    private readonly Dictionary<long, List<int>> _lit = [];
    private readonly List<List<int>> _spareLit = [];
    private readonly List<uint> _players = [];
    private int[] _marks = [];
    private int _generation;

    /// <param name="bounds">Карта партии.</param>
    /// <param name="layers">Слои мира: в них переносятся изменения (нужны слои <see cref="Fog.Sight"/> и <see cref="Fog.Explored"/>).</param>
    public Vision(MapBounds bounds, LayerSet? layers)
    {
        Bounds = bounds;
        _layers = layers;
        if (layers is not null)
        {
            _sight = layers.Find(Fog.Sight) ?? -1;
            _explored = layers.Find(Fog.Explored) ?? -1;
        }
    }

    public MapBounds Bounds { get; }

    /// <summary>Союзы на последнем пересчёте.</summary>
    public Teams Teams { get; } = new();

    private int Size => Math.Max(0, Bounds.Width * Bounds.Height);

    /// <summary>Игроки с обзором, по возрастанию.</summary>
    public IEnumerable<uint> Players => _sights.Keys;

    /// <summary>Заводит игроку обзор (всё не разведано), если его нет.</summary>
    public void AddPlayer(uint player) => SightOf(player);

    /// <summary>Обзор игрока из его слоя «разведано» (загрузка сохранения): видимое пересчитает <see cref="Update"/>.</summary>
    public void Restore(uint player)
    {
        var sight = SightOf(player);
        sight.Lit.Clear();
        if (_layers is null || _explored < 0)
            return;
        var cells = sight.Cells;
        var owner = LayerOwner.Player(player);
        var width = Bounds.Width;
        for (var i = 0; i < cells.Length; i++)
            cells[i] = _layers.Get(_explored, owner, Bounds.Left + i % width, Bounds.Top + i / width, 0) != 0 ? Known : Unseen;
    }

    /// <summary>Вся карта игроку разведана (/reveal).</summary>
    public void Reveal(uint player)
    {
        var sight = SightOf(player);
        var cells = sight.Cells;
        for (var i = 0; i < cells.Length; i++)
            if (cells[i] == Unseen)
            {
                cells[i] = Known;
                Mirror(player, i, _explored, 1);
            }
        sight.Changed++;
    }

    /// <summary>Игрок начинает заново: всё не разведано.</summary>
    public void Forget(uint player)
    {
        if (!_sights.TryGetValue(player, out var sight))
            return;
        var cells = sight.Cells;
        for (var i = 0; i < cells.Length; i++)
        {
            if (cells[i] == Shown)
                Mirror(player, i, _sight, 0);
            if (cells[i] != Unseen)
                Mirror(player, i, _explored, 0);
            cells[i] = Unseen;
        }
        sight.Lit.Clear();
        sight.Changed++;
    }

    /// <summary>Растёт, когда у игрока поменялось видимое.</summary>
    public long Changed(uint player) => _sights.TryGetValue(player, out var sight) ? sight.Changed : 0;

    /// <summary>Тайлы игрока построчно (0, 1, 2); нет — пусто.</summary>
    public ReadOnlySpan<byte> Cells(uint player) => _sights.TryGetValue(player, out var sight) ? sight.Cells : [];

    /// <summary>Видит ли игрок тайл сейчас.</summary>
    public bool Sees(uint player, double x, double y) => Cell(player, x, y) == Shown;

    /// <summary>Бывал ли тайл в виду игрока.</summary>
    public bool Explored(uint player, double x, double y) => Cell(player, x, y) >= Known;

    /// <summary>Видит ли игрок хоть один тайл прямоугольника w × h с левым верхним углом (x, y): основание здания.</summary>
    public bool SeesArea(uint player, double x, double y, double w, double h)
    {
        if (!_sights.TryGetValue(player, out var sight))
            return false;
        var cells = sight.Cells;
        for (var dy = 0; dy < h; dy++)
            for (var dx = 0; dx < w; dx++)
                if (Index(x + dx, y + dy) is >= 0 and var index && cells[index] == Shown)
                    return true;
        return false;
    }

    /// <summary>Пересчёт без тика (тесты): вспышки с until &gt; 0 светят.</summary>
    public void Update(GameWorld world) => Update(world, 0);

    /// <summary>
    /// Пересчёт: обзор всех, у кого есть сущности на карте, и всех, у кого он уже есть. Видимое гаснет до
    /// разведанного, глаза и вспышки группы зажигают свои круги.
    /// </summary>
    public void Update(GameWorld world, ulong tick)
    {
        Teams.Read(world);
        Collect(world, tick);
        foreach (var lit in _lit.Values)
        {
            lit.Clear();
            _spareLit.Add(lit);
        }
        _lit.Clear();
        _players.Clear();
        _players.AddRange(_sights.Keys);
        foreach (var player in _players)
        {
            var group = Teams.Group(player);
            if (!_lit.TryGetValue(group, out var lit))
                _lit[group] = lit = Light(_eyes.GetValueOrDefault(group));
            Apply(player, lit);
        }
    }

    /// <summary>Глаза по группам обзора и вспышки всем; заводит обзор игрокам с сущностями на карте.</summary>
    private void Collect(GameWorld world, ulong tick)
    {
        foreach (var list in _eyes.Values)
            list.Clear();
        _everyone.Clear();
        var owners = world.Store(Builtin.Owner);
        var positions = world.Store(Builtin.Position);
        var players = owners.Column(0);
        // Игроки — у всех, у кого что-то есть на карте (так разведанное копится и у тех, кто не в сети).
        var owned = owners.Entities;
        for (var row = 0; row < owned.Length; row++)
            if (players[row] >= 1 && players[row] <= uint.MaxValue && positions.Contains(owned[row]))
                SightOf((uint)players[row]);
        // И у игроков со счётом (Team): без юнитов они видят глазами союзников.
        var teams = world.Store(Builtin.Team).Column(0);
        foreach (var player in teams)
            if (player >= 1 && player <= uint.MaxValue)
                SightOf((uint)player);

        var sight = world.Store(Builtin.Sight);
        var radii = sight.Column(0);
        var eyes = sight.Entities;
        for (var row = 0; row < eyes.Length; row++)
        {
            var entity = eyes[row];
            if (!(radii[row] > 0) || Owner(owners, entity) is not { } player || Mounted(world, entity) || Place(world, positions, entity) is not { } at)
                continue;
            Group(Teams.Group(player)).Add(new Eye(at.X, at.Y, radii[row]));
        }

        var reveal = world.Store(Builtin.Reveal);
        var radius = reveal.Column(0);
        var until = reveal.Column(1);
        var all = reveal.Column(2);
        var flashes = reveal.Entities;
        for (var row = 0; row < flashes.Length; row++)
        {
            var entity = flashes[row];
            if (!(radius[row] > 0) || !(until[row] > tick) || positions.RowOf(entity) is var at && at < 0)
                continue;
            var eye = new Eye(positions.Column(0)[at], positions.Column(1)[at], radius[row]);
            if (all[row] != 0)
                _everyone.Add(eye);
            else if (Owner(owners, entity) is { } player)
                Group(Teams.Group(player)).Add(eye);
        }
    }

    private List<Eye> Group(long group)
    {
        if (!_eyes.TryGetValue(group, out var list))
            _eyes[group] = list = [];
        return list;
    }

    private static uint? Owner(ComponentStore owners, uint entity)
    {
        var row = owners.RowOf(entity);
        if (row < 0)
            return null;
        var player = owners.Column(0)[row];
        return player >= 1 && player <= uint.MaxValue ? (uint)player : null;
    }

    /// <summary>На носителе: глаз носителя видит за неё (TS: Attached не видит).</summary>
    private static bool Mounted(GameWorld world, uint entity) => world.TryNumber(entity, Builtin.Mount, 0, out var parent) && parent != 0;

    /// <summary>Середина глаза: место, у здания — середина основания.</summary>
    private static (double X, double Y)? Place(GameWorld world, ComponentStore positions, uint entity)
    {
        var row = positions.RowOf(entity);
        if (row < 0)
            return null;
        var (x, y) = (positions.Column(0)[row], positions.Column(1)[row]);
        if (world.TryNumber(entity, Builtin.Footprint, 0, out var w) && world.TryNumber(entity, Builtin.Footprint, 1, out var h))
            (x, y) = (x + w / 2, y + h / 2);
        return (x, y);
    }

    /// <summary>Тайлы, открытые глазами группы и вспышками всем, — без повторов.</summary>
    private List<int> Light(List<Eye>? eyes)
    {
        List<int> lit;
        if (_spareLit.Count > 0)
        {
            lit = _spareLit[^1];
            _spareLit.RemoveAt(_spareLit.Count - 1);
        }
        else
        {
            lit = [];
        }
        if (_marks.Length != Size)
            _marks = new int[Size];
        if (++_generation == int.MaxValue)
        {
            Array.Clear(_marks);
            _generation = 1;
        }
        foreach (var eye in eyes ?? [])
            Stamp(lit, eye);
        foreach (var eye in _everyone)
            Stamp(lit, eye);
        return lit;
    }

    /// <summary>Круг глаза — в открытое.</summary>
    private void Stamp(List<int> lit, Eye eye)
    {
        var bounds = Bounds;
        var width = bounds.Width;
        var (x, y, radius) = (eye.X, eye.Y, eye.Radius);
        var reach = radius * radius;
        var fromY = Math.Max(bounds.Top, (int)Math.Floor(y - radius));
        var toY = Math.Min(bounds.Bottom - 1, (int)Math.Floor(y + radius));
        for (var tileY = fromY; tileY <= toY; tileY++)
        {
            var dy = tileY + 0.5 - y;
            var span = Math.Sqrt(Math.Max(0, reach - dy * dy));
            var fromX = Math.Max(bounds.Left, (int)Math.Floor(x - span));
            var toX = Math.Min(bounds.Right - 1, (int)Math.Floor(x + span));
            var start = (tileY - bounds.Top) * width - bounds.Left;
            for (var tileX = fromX; tileX <= toX; tileX++)
            {
                var index = start + tileX;
                if (_marks[index] == _generation)
                    continue;
                _marks[index] = _generation;
                lit.Add(index);
            }
        }
    }

    /// <summary>Новое открытое игрока: прежнее гаснет до разведанного, изменения — в слои.</summary>
    private void Apply(uint player, List<int> lit)
    {
        var sight = _sights[player];
        var cells = sight.Cells;
        var changes = 0;
        foreach (var index in lit)
        {
            var was = cells[index];
            if (was != Shown)
            {
                changes++;
                Mirror(player, index, _sight, 1);
                if (was == Unseen)
                    Mirror(player, index, _explored, 1);
            }
            cells[index] = Relit;
        }
        foreach (var index in sight.Lit)
            if (cells[index] == Shown)
            {
                cells[index] = Known;
                changes++;
                Mirror(player, index, _sight, 0);
            }
        foreach (var index in lit)
            cells[index] = Shown;
        sight.Lit.Clear();
        sight.Lit.AddRange(lit);
        if (changes > 0)
            sight.Changed++;
    }

    private Sight SightOf(uint player)
    {
        if (!_sights.TryGetValue(player, out var sight))
            _sights[player] = sight = new Sight(new byte[Size]);
        return sight;
    }

    /// <summary>Тайл — в слой игрока.</summary>
    private void Mirror(uint player, int index, int layer, double value)
    {
        if (_layers is null || layer < 0)
            return;
        var width = Bounds.Width;
        _layers.Set(layer, LayerOwner.Player(player), Bounds.Left + index % width, Bounds.Top + index / width, 0, value);
    }

    private int Index(double x, double y)
    {
        var (tx, ty) = ((int)Math.Floor(x), (int)Math.Floor(y));
        return Bounds.Contains(tx, ty) ? (ty - Bounds.Top) * Bounds.Width + tx - Bounds.Left : -1;
    }

    private int Cell(uint player, double x, double y) =>
        Index(x, y) is >= 0 and var index && _sights.TryGetValue(player, out var sight) ? sight.Cells[index] : Unseen;

    private readonly record struct Eye(double X, double Y, double Radius);

    /// <summary>Обзор игрока: тайлы, открытые при прошлом пересчёте, и счёт перемен.</summary>
    private sealed class Sight(byte[] cells)
    {
        public byte[] Cells { get; } = cells;
        public List<int> Lit { get; } = [];
        public long Changed;
    }
}
