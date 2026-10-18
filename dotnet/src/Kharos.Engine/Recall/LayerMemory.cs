using Kharos.Engine.World;

namespace Kharos.Engine.Recall;

/// <summary>Обзор для памяти: кто из игроков есть и видит ли игрок тайл сейчас.</summary>
public interface IRecallVision
{
    /// <summary>Игроки с обзором, по возрастанию.</summary>
    IEnumerable<uint> Players { get; }

    /// <summary>Видит ли игрок точку сейчас.</summary>
    bool Sees(uint player, double x, double y);
}

/// <summary>Тайл слоя: x и y.</summary>
public readonly record struct Tile(int X, int Y);

/// <summary>
/// Память запоминаемого слоя мира (recall.ts, crates/sim/src/recall.rs, docs/layers.md): слой у всех один, но его
/// изменения игрок узнаёт, только увидев, — в тумане войны тайл остаётся для него таким, каким он его видел последним.
/// Своё и союзное он знает всегда. Так помнятся правки земли (<see cref="LandMemory"/>) и покрытие
/// (<see cref="PavingMemory"/>): одно правило, два слоя.
/// <para>
/// Значение тайла — одно число: у земли хранимые байты тайла (<see cref="LandMemory.Pack"/>), у покрытия — его код.
/// Помнится только расхождение с общим (<see cref="Common"/>) — с тем, что игрок знает, ничего не видев.
/// </para>
/// </summary>
public abstract class LayerMemory
{
    /// <summary>Раз во сколько тиков игроки узнают увиденное: обзор и так пересчитывается раз в столько.</summary>
    public const int LearnTicks = 4;

    private readonly SortedDictionary<uint, Dictionary<Tile, uint>> _known = [];

    /// <summary>Где знание игрока может расходиться со слоем: изменилось с тех пор, как он смотрел.</summary>
    private readonly SortedDictionary<uint, HashSet<Tile>> _stale = [];
    private readonly SortedDictionary<uint, HashSet<Tile>> _learned = [];
    private readonly HashSet<Tile> _changed = [];

    /// <summary>Что знает каждый игрок иначе, чем общее.</summary>
    public IReadOnlyDictionary<uint, Dictionary<Tile, uint>> Known => _known;

    /// <summary>Значение тайла сейчас.</summary>
    protected abstract uint Actual(Tile tile);

    /// <summary>Что знает о тайле игрок, ничего о нём не узнававший: исходное у земли, «ничего» у покрытия.</summary>
    public abstract uint Common(Tile tile, uint actual);

    /// <summary>Чьё это значение — тогда хозяин и союзники знают его сразу; null — ничьё.</summary>
    protected virtual uint? Owner(uint value) => null;

    /// <summary>Нужны ли союзы: у слоя есть хозяева значений.</summary>
    protected virtual bool HasOwners => false;

    /// <summary>Тайлы, где слой может расходиться с общим: их сверяет новый игрок.</summary>
    protected abstract IEnumerable<Tile> Tiles();

    /// <summary>Нечего сверять (у земли — пока её не правили).</summary>
    protected virtual bool Idle => false;

    /// <summary>Тайл слоя изменился: его сверят все игроки.</summary>
    public void Change(Tile tile) => _changed.Add(tile);

    /// <summary>Знание игрока из сохранения.</summary>
    public void Know(uint player, Tile tile, uint value)
    {
        if (!_known.TryGetValue(player, out var known))
            _known[player] = known = [];
        known[tile] = value;
    }

    /// <summary>Что знает игрок о тайле.</summary>
    public uint KnownValue(uint player, Tile tile) =>
        _known.TryGetValue(player, out var known) && known.TryGetValue(tile, out var value) ? value : Common(tile, Actual(tile));

    /// <summary>Тайл забыт: правка редактора стала исходной картой, её видят все.</summary>
    public virtual void Forget(Tile tile)
    {
        foreach (var known in _known.Values)
            known.Remove(tile);
    }

    /// <summary>Игрок начинает заново: что он знал, забыто.</summary>
    public void ForgetPlayer(uint player)
    {
        _known.Remove(player);
        _stale.Remove(player);
        _learned.Remove(player);
    }

    /// <summary>Что игрок узнал с прошлого раза — по всем игрокам; список очищается. Тайлы по возрастанию (y, x).</summary>
    public SortedDictionary<uint, List<Tile>> TakeLearned()
    {
        var learned = new SortedDictionary<uint, List<Tile>>();
        foreach (var (player, tiles) in _learned)
        {
            var list = tiles.ToList();
            list.Sort(static (a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
            learned[player] = list;
        }
        _learned.Clear();
        return learned;
    }

    /// <summary>Всё, что игрок знает иначе, чем общее, — по возрастанию (y, x): при подключении.</summary>
    public List<Tile> KnownTiles(uint player)
    {
        var tiles = _known.TryGetValue(player, out var known) ? known.Keys.ToList() : [];
        tiles.Sort(static (a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
        return tiles;
    }

    /// <summary>Заводит игроку сверку: и того, что он знает, и всего, где слой расходится с общим.</summary>
    private void Track(uint player)
    {
        if (_stale.ContainsKey(player))
            return;
        if (!_known.ContainsKey(player))
            _known[player] = [];
        var stale = new HashSet<Tile>(_known[player].Keys);
        foreach (var tile in Tiles())
            stale.Add(tile);
        _stale[player] = stale;
    }

    /// <summary>
    /// Система тика, после обзора (<c>updateRecall</c>): изменившееся ждёт сверки у каждого игрока; своё и союзное он
    /// узнаёт в тот же тик, чужое — увидев, раз в <see cref="LearnTicks"/>.
    /// </summary>
    public void Update(IRecallVision vision, GameWorld world, ulong tick)
    {
        // Земля без правок: и сверять нечего, и изменённое копится до первой.
        if (Idle)
            return;
        var changed = _changed.ToList();
        _changed.Clear();
        foreach (var stale in _stale.Values)
            foreach (var tile in changed)
                stale.Add(tile);
        foreach (var player in vision.Players)
            Track(player);
        var look = tick % LearnTicks == 0;
        foreach (var player in _stale.Keys.ToList())
        {
            var stale = _stale[player];
            if (stale.Count == 0)
                continue;
            var team = HasOwners ? Allies(world, player) : null;
            if (changed.Count > 0)
                Learn(vision, team, player, stale, changed, false);
            if (look && stale.Count > 0)
                Learn(vision, team, player, stale, stale.ToList(), true);
        }
    }

    /// <summary>Игрок узнаёт тайлы из тех, что ждут у него сверки: свои и союзные всегда, чужие — если look и он их видит.</summary>
    private void Learn(IRecallVision vision, HashSet<uint>? team, uint player, HashSet<Tile> stale, List<Tile> tiles, bool look)
    {
        var known = _known[player];
        bool Ours(uint? owner) => owner is { } who && (who == player || team?.Contains(who) == true);
        foreach (var tile in tiles)
        {
            if (!stale.Contains(tile))
                continue;
            var actual = Actual(tile);
            var common = Common(tile, actual);
            var was = known.TryGetValue(tile, out var value) ? value : common;
            if (actual != was)
            {
                if (!Ours(Owner(actual)) && !Ours(Owner(was)) && !(look && vision.Sees(player, tile.X, tile.Y)))
                    continue;
                if (actual == common)
                    known.Remove(tile);
                else
                    known[tile] = actual;
                if (!_learned.TryGetValue(player, out var learned))
                    _learned[player] = learned = [];
                learned.Add(tile);
            }
            stale.Remove(tile);
        }
    }

    /// <summary>Союзники игрока (<c>alliesIn</c>): с тем же главой союза (<c>Team.head</c> ≥ 0); сам игрок не в списке.</summary>
    public static HashSet<uint> Allies(GameWorld world, uint player)
    {
        var allies = new HashSet<uint>();
        var teams = world.Store(Builtin.Team);
        if (teams.Count == 0)
            return allies;
        var players = teams.Column(0);
        var heads = teams.Column(1);
        double head = -1;
        for (var row = 0; row < players.Length; row++)
            if (players[row] == player)
                head = heads[row];
        if (head < 0)
            return allies;
        for (var row = 0; row < players.Length; row++)
            if (heads[row] == head && players[row] != player && players[row] >= 1)
                allies.Add((uint)players[row]);
        return allies;
    }

    // Сохранение: игроки и тайлы по возрастанию, чтобы файл был одинаков у одинаковых партий.

    internal void WriteKnown(BinaryWriter w)
    {
        w.Write7BitEncodedInt(_known.Count);
        foreach (var (player, known) in _known)
        {
            w.Write7BitEncodedInt64(player);
            w.Write7BitEncodedInt(known.Count);
            foreach (var tile in KnownTiles(player))
            {
                w.Write(tile.X);
                w.Write(tile.Y);
                w.Write(known[tile]);
            }
        }
    }

    internal void ReadKnown(BinaryReader r)
    {
        for (var players = Count(r); players > 0; players--)
        {
            var player = (uint)r.Read7BitEncodedInt64();
            if (player == 0)
                throw new FormatException("память: игрок 0");
            if (!_known.ContainsKey(player))
                _known[player] = [];
            for (var tiles = Count(r); tiles > 0; tiles--)
                Know(player, new Tile(r.ReadInt32(), r.ReadInt32()), r.ReadUInt32());
        }
    }

    internal static int Count(BinaryReader r)
    {
        var count = r.Read7BitEncodedInt();
        return count >= 0 && count <= r.BaseStream.Length - r.BaseStream.Position ? count : throw new FormatException("память: счёт больше данных");
    }
}
