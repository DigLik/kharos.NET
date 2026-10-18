using Kharos.Engine.Layers;
using Kharos.Engine.World;
using Kharos.Protocol;

namespace Kharos.Host;

/// <summary>
/// Обзор игроков — система движка (crates/world/src/vision.rs, vision.ts): что игрок видит сейчас и что разведал. Глаз —
/// компонент движка <c>Sight { radius }</c> у сущности с <c>Owner</c> и <c>Position</c>: круг в radius тайлов (ряды,
/// чья середина в круге; в ряду — тайлы от ⌊x − s⌋ до ⌊x + s⌋, s — полухорда на середине ряда). Пересчёт — раз в <see cref="Ticks"/> тиков. По игроку — тайлы карты построчно:
/// 0 — не разведан, 1 — разведан, 2 — виден сейчас. Изменённые тайлы переносятся в слои игрока
/// <see cref="Fog.Sight"/> и <see cref="Fog.Explored"/>: так туман сохраняется и уходит клиенту изменёнными чанками.
/// </summary>
public sealed class Vision
{
    /// <summary>Раз во сколько тиков пересчитывается обзор (на тиках с остатком 1, как в пилоте).</summary>
    public const int Ticks = 4;

    private readonly SortedDictionary<uint, byte[]> _cells = [];
    private readonly Dictionary<uint, byte[]> _fresh = [];
    private readonly Dictionary<uint, long> _changed = [];
    private readonly List<byte[]> _spare = [];
    private readonly LayerSet? _layers;
    private readonly int _sight = -1, _explored = -1;

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

    private int Size => Math.Max(0, Bounds.Width * Bounds.Height);

    /// <summary>Игроки с обзором, по возрастанию.</summary>
    public IEnumerable<uint> Players => _cells.Keys;

    /// <summary>Заводит игроку обзор (всё не разведано), если его нет.</summary>
    public void AddPlayer(uint player)
    {
        if (!_cells.ContainsKey(player))
            _cells[player] = new byte[Size];
    }

    /// <summary>Растёт, когда у игрока поменялось видимое.</summary>
    public long Changed(uint player) => _changed.GetValueOrDefault(player);

    /// <summary>Тайлы игрока построчно (0, 1, 2); нет — пусто.</summary>
    public ReadOnlySpan<byte> Cells(uint player) => _cells.TryGetValue(player, out var cells) ? cells : [];

    /// <summary>Видит ли игрок тайл сейчас.</summary>
    public bool Sees(uint player, double x, double y) => Cell(player, x, y) == 2;

    /// <summary>Бывал ли тайл в виду игрока.</summary>
    public bool Explored(uint player, double x, double y) => Cell(player, x, y) >= 1;

    /// <summary>Пересчёт: видимое гаснет до разведанного, глаза каждого игрока зажигают свои круги.</summary>
    public void Update(GameWorld world)
    {
        var bounds = Bounds;
        var width = bounds.Width;
        var sight = world.Store(Builtin.Sight);
        var owners = world.Store(Builtin.Owner);
        var positions = world.Store(Builtin.Position);
        var radii = sight.Column(0);
        var players = owners.Column(0);
        var xs = positions.Column(0);
        var ys = positions.Column(1);
        var entities = sight.Entities;
        uint lastPlayer = 0;
        byte[]? cells = null;
        for (var row = 0; row < entities.Length; row++)
        {
            var radius = radii[row];
            var ownerRow = owners.RowOf(entities[row]);
            var positionRow = positions.RowOf(entities[row]);
            if (ownerRow < 0 || positionRow < 0 || !(radius > 0) || !(players[ownerRow] >= 1))
                continue;
            var player = (uint)players[ownerRow];
            if (cells is null || player != lastPlayer)
            {
                cells = Fresh(player);
                lastPlayer = player;
            }
            var (x, y) = (xs[positionRow], ys[positionRow]);
            var reach = radius * radius;
            var fromY = Math.Max(bounds.Top, (int)Math.Floor(y - radius));
            var toY = Math.Min(bounds.Bottom - 1, (int)Math.Floor(y + radius));
            for (var tileY = fromY; tileY <= toY; tileY++)
            {
                var dy = tileY + 0.5 - y;
                var span = Math.Sqrt(Math.Max(0, reach - dy * dy));
                var fromX = Math.Max(bounds.Left, (int)Math.Floor(x - span));
                var toX = Math.Min(bounds.Right - 1, (int)Math.Floor(x + span));
                if (fromX > toX)
                    continue;
                var start = (tileY - bounds.Top) * width - bounds.Left;
                cells.AsSpan(start + fromX, toX - fromX + 1).Fill(2);
            }
        }
        // Игрок без глаз: видимое гаснет, разведанное остаётся.
        foreach (var player in _cells.Keys)
            Fresh(player);
        foreach (var (player, fresh) in _fresh)
        {
            if (!_cells.TryGetValue(player, out var old))
                old = new byte[Size];
            if (!old.AsSpan().SequenceEqual(fresh))
            {
                _changed[player] = _changed.GetValueOrDefault(player) + 1;
                Mirror(player, old, fresh);
                _cells[player] = fresh;
                _spare.Add(old);
            }
            else
            {
                _cells[player] = old;
                _spare.Add(fresh);
            }
        }
        _fresh.Clear();
    }

    /// <summary>Новый обзор игрока за этот пересчёт: прежний, где видимое погашено до разведанного.</summary>
    private byte[] Fresh(uint player)
    {
        if (_fresh.TryGetValue(player, out var fresh))
            return fresh;
        if (_spare.Count > 0)
        {
            fresh = _spare[^1];
            _spare.RemoveAt(_spare.Count - 1);
        }
        else
        {
            fresh = new byte[Size];
        }
        if (_cells.TryGetValue(player, out var old))
            for (var i = 0; i < fresh.Length; i++)
                fresh[i] = old[i] != 0 ? (byte)1 : (byte)0;
        else
            Array.Clear(fresh);
        _fresh[player] = fresh;
        return fresh;
    }

    /// <summary>Изменённые тайлы — в слои игрока.</summary>
    private void Mirror(uint player, byte[] old, byte[] fresh)
    {
        if (_layers is null)
            return;
        var owner = LayerOwner.Player(player);
        var width = Bounds.Width;
        for (var i = 0; i < fresh.Length; i++)
        {
            var (was, now) = (old[i], fresh[i]);
            if (was == now)
                continue;
            var (x, y) = (Bounds.Left + i % width, Bounds.Top + i / width);
            if (_sight >= 0 && (was == 2) != (now == 2))
                _layers.Set(_sight, owner, x, y, 0, now == 2 ? 1 : 0);
            if (_explored >= 0 && (was >= 1) != (now >= 1))
                _layers.Set(_explored, owner, x, y, 0, now >= 1 ? 1 : 0);
        }
    }

    private int Cell(uint player, double x, double y)
    {
        var (tx, ty) = ((int)Math.Floor(x), (int)Math.Floor(y));
        if (!Bounds.Contains(tx, ty) || !_cells.TryGetValue(player, out var cells))
            return 0;
        return cells[(ty - Bounds.Top) * Bounds.Width + tx - Bounds.Left];
    }
}
