using Kharos.Engine.World;
using Kharos.Protocol;

namespace Kharos.Host;

/// <summary>
/// Что подключение знает о мире (host.ts: Known, begin, take) и что ему слать в этот тик. Обязательное уходит сразу:
/// сущности, вошедшие в обзор (целиком), пропавшие и ушедшие из виду, смена состава компонентов, новые строки, слои и
/// сигналы. Изменения полей знакомых сущностей копятся масками и уходят по очереди приоритета, пока хватает байт и
/// времени: приоритет — возраст × вес (на экране ×8, рядом ×3, едет ×2); корзины по четверти удвоения, как
/// bucketOf в TS. Что не влезло, ждёт и тяжелеет. Пришедшая с опозданием сущность получает span — за сколько тиков
/// реплике вести её к новому месту. Помнимое (<see cref="IViewFilter.Remembers"/>) вне обзора замирает: остаётся у
/// клиента без изменений, погибшее — пока зритель не увидит его место.
/// </summary>
internal sealed class PeerSync
{
    /// <summary>Корзин в очереди.</summary>
    private const int Buckets = 64;

    /// <summary>Веса очереди: на экране, рядом с ним, едет.</summary>
    private const int OnScreen = 8, NearScreen = 3, Moving = 2;

    /// <summary>Запас вокруг экрана — доля его стороны, но не меньше стольких тайлов.</summary>
    private const float Margin = 0.25f, MarginLeast = 6;

    /// <summary>Дольше скольких тиков отставание не сглаживается: сущность встаёт на место сразу.</summary>
    private const int MaxSpan = 40;

    private readonly Dictionary<uint, Known> _known = [];
    private readonly HashSet<uint> _dirty = [];
    private readonly List<uint>[] _queue = new List<uint>[Buckets];
    private readonly List<uint> _gone = [];
    private int _bucket, _at, _pass;

    public PeerSync()
    {
        for (var b = 0; b < Buckets; b++)
            _queue[b] = [];
    }

    /// <summary>Сколько сущностей подключение знает (с замершими и призраками).</summary>
    public int KnownCount => _known.Count;

    /// <summary>Сколько изменённых сущностей ждёт очереди.</summary>
    public int Waiting => _dirty.Count;

    /// <summary>Сколько осталось в очереди этого тика.</summary>
    public int Queued { get; private set; }

    /// <summary>Знает ли подключение сущность (для тестов).</summary>
    public bool Knows(uint entity) => _known.ContainsKey(entity);

    /// <summary>Изменения тика — знакомым сущностям; пропавшие — удаление (замершие становятся призраками).</summary>
    public void Merge(GameWorld world, WorldDelta delta, DeltaWriter w)
    {
        foreach (var entity in delta.Destroyed)
        {
            if (!_known.TryGetValue(entity, out var known))
                continue;
            if (known.Frozen)
            {
                known.Dead = true;
                known.Clear();
                _dirty.Remove(entity);
                continue;
            }
            w.Remove(entity);
            _known.Remove(entity);
            _dirty.Remove(entity);
        }
        // Появившиеся в мире, но уже знакомые (подключение вошло посреди накопленного: снимок их взял) — заново
        // целиком: их правки после снимка в дельте не числятся.
        foreach (var entity in delta.Created)
        {
            if (!_known.TryGetValue(entity, out var known) || known.Dead || !world.Alive(entity))
                continue;
            w.Create(world, entity);
            known.Clear();
            known.Flushed = delta.Tick;
            _dirty.Remove(entity);
        }
        foreach (var change in delta.Full)
            Pend(change.Entity, change.Component, ulong.MaxValue, Kind.Full, delta.Tick);
        foreach (var change in delta.Removed)
            Pend(change.Entity, change.Component, 0, Kind.Drop, delta.Tick);
        foreach (var field in delta.Fields)
            Pend(field.Entity, field.Component, field.Mask, Kind.Fields, delta.Tick);
    }

    /// <summary>
    /// Обзор: вошедшие в него — целиком, ушедшие — удаление или замирание, призраки — удаление, когда видно их место.
    /// visible[i] — видна ли alive[i]. Затем — обязательная смена состава компонентов.
    /// </summary>
    public void View(GameWorld world, IViewFilter filter, ReadOnlySpan<uint> alive, ReadOnlySpan<bool> visible, ulong tick, DeltaWriter w)
    {
        _pass++;
        for (var i = 0; i < alive.Length; i++)
        {
            var entity = alive[i];
            if (_known.TryGetValue(entity, out var known))
            {
                if (visible[i])
                {
                    if (known.Frozen)
                    {
                        // Вернулась в обзор: встаёт на место сразу, без сглаживания.
                        known.Frozen = false;
                        known.MotionSince = 0;
                    }
                    known.Pass = _pass;
                }
                else if (filter.Remembers(entity))
                {
                    known.Frozen = true;
                    known.Pass = _pass;
                }
            }
            else if (visible[i])
            {
                w.Create(world, entity);
                known = new Known { Flushed = tick, Pass = _pass };
                Place(world, entity, known);
                _known[entity] = known;
            }
        }
        _gone.Clear();
        foreach (var (entity, known) in _known)
        {
            if (known.Pass == _pass)
                continue;
            if (known.Dead && !filter.Sees(known.X, known.Y))
                continue;
            _gone.Add(entity);
        }
        foreach (var entity in _gone)
        {
            w.Remove(entity);
            _known.Remove(entity);
            _dirty.Remove(entity);
        }
        // Состав компонентов — сразу: без него поля следующих тиков не к чему применить.
        _gone.Clear();
        foreach (var entity in _dirty)
            if (_known[entity] is { Frozen: false, Structural: > 0 })
                _gone.Add(entity);
        foreach (var entity in _gone)
            Flush(world, entity, tick, w);
    }

    /// <summary>Очередь тика: изменённые знакомые сущности в обзоре — по корзинам приоритета.</summary>
    public void Begin(GameWorld world, ViewBox? box, ulong tick)
    {
        foreach (var list in _queue)
            list.Clear();
        Queued = 0;
        var position = world.Store(Builtin.Position);
        var (left, top, right, bottom) = (0f, 0f, 0f, 0f);
        if (box is { } view)
        {
            var mx = MathF.Max(MarginLeast, (view.Right - view.Left) * Margin);
            var my = MathF.Max(MarginLeast, (view.Bottom - view.Top) * Margin);
            (left, top, right, bottom) = (view.Left - mx, view.Top - my, view.Right + mx, view.Bottom + my);
        }
        foreach (var entity in _dirty)
        {
            var known = _known[entity];
            if (known.Frozen || known.Dead)
                continue;
            var weight = 1;
            if (box is { } screen)
            {
                var row = position.RowOf(entity);
                if (row < 0)
                {
                    weight = NearScreen;
                }
                else
                {
                    var (x, y) = (position.Column(0)[row], position.Column(1)[row]);
                    if (screen.Contains(x, y))
                        weight = OnScreen;
                    else if (x >= left && x <= right && y >= top && y <= bottom)
                        weight = NearScreen;
                }
            }
            if (known.MotionSince > 0)
                weight *= Moving;
            var age = (long)Math.Max(1, (long)(tick - known.Flushed));
            _queue[BucketOf(age * weight)].Add(entity);
            Queued++;
        }
        // Внутри корзины — по номерам: разности номеров в группах полей короче.
        foreach (var list in _queue)
            if (list.Count > 1)
                list.Sort();
        _bucket = Buckets - 1;
        _at = 0;
    }

    /// <summary>Шлёт до count сущностей из верха очереди.</summary>
    public void Take(GameWorld world, int count, ulong tick, DeltaWriter w)
    {
        while (count > 0 && Queued > 0)
        {
            var list = _queue[_bucket];
            if (_at >= list.Count)
            {
                _bucket--;
                _at = 0;
                continue;
            }
            Flush(world, list[_at++], tick, w);
            Queued--;
            count--;
        }
    }

    /// <summary>Всё накопленное о сущности — в сообщение.</summary>
    private void Flush(GameWorld world, uint entity, ulong tick, DeltaWriter w)
    {
        if (!_dirty.Remove(entity) || !_known.TryGetValue(entity, out var known))
            return;
        for (var at = 0; at < known.Count; at++)
        {
            var pending = known.Pending[at];
            switch (pending.Kind)
            {
                case Kind.Full:
                    if (world.Has(entity, pending.Component))
                        w.Add(world, entity, pending.Component);
                    break;
                case Kind.Drop:
                    w.Drop(entity, pending.Component);
                    break;
                default:
                    if (world.Has(entity, pending.Component))
                        w.Fields(world, entity, pending.Component, pending.Mask);
                    break;
            }
        }
        if (known.MotionSince > 0)
        {
            var span = (long)(tick - known.MotionSince) + 1;
            if (span is > 1 and <= MaxSpan)
                w.Span(entity, (int)span);
        }
        known.Clear();
        known.Flushed = tick;
        Place(world, entity, known);
    }

    private void Pend(uint entity, int component, ulong mask, Kind kind, ulong tick)
    {
        if (!_known.TryGetValue(entity, out var known) || known.Dead)
            return;
        known.Add(component, mask, kind);
        if (kind == Kind.Fields && component is Builtin.Position or Builtin.Facing && known.MotionSince == 0)
            known.MotionSince = tick;
        _dirty.Add(entity);
    }

    private static void Place(GameWorld world, uint entity, Known known)
    {
        if (world.TryNumber(entity, Builtin.Position, 0, out var x) && world.TryNumber(entity, Builtin.Position, 1, out var y))
            (known.X, known.Y) = (x, y);
    }

    /// <summary>Корзина приоритета: до 4 — сам приоритет, дальше — по четыре на удвоение (bucketOf в host.ts).</summary>
    private static int BucketOf(long priority)
    {
        if (priority < 4)
            return (int)priority;
        var high = 63 - (int)long.LeadingZeroCount(priority);
        return (int)Math.Min(Buckets - 1, high * 4 + ((priority >> (high - 2)) & 3));
    }

    private enum Kind : byte
    {
        Fields,
        Full,
        Drop,
    }

    private struct Pending
    {
        public int Component;
        public ulong Mask;
        public Kind Kind;
    }

    /// <summary>Что подключение знает о сущности.</summary>
    private sealed class Known
    {
        public Pending[] Pending = [];
        public int Count;

        /// <summary>Сколько в ожидании смен состава (добавлен или снят компонент).</summary>
        public int Structural;

        /// <summary>Тик последней отправки.</summary>
        public ulong Flushed;

        /// <summary>Тик первого неотправленного сдвига; 0 — нет.</summary>
        public ulong MotionSince;

        /// <summary>Проход обзора, где сущность последний раз была у подключения.</summary>
        public int Pass;

        /// <summary>Вне обзора, но помнится: изменения копятся и не шлются.</summary>
        public bool Frozen;

        /// <summary>Погибла вне обзора: призрак до тех пор, пока зритель не увидит её место.</summary>
        public bool Dead;

        /// <summary>Место, каким его знает клиент.</summary>
        public double X = double.NaN, Y = double.NaN;

        public void Add(int component, ulong mask, Kind kind)
        {
            for (var at = 0; at < Count; at++)
            {
                ref var pending = ref Pending[at];
                if (pending.Component != component)
                    continue;
                // Целиком поглощает поля; снятие — всё прежнее; поля после целого — уже в целом.
                if (kind == Kind.Fields)
                {
                    if (pending.Kind == Kind.Fields)
                        pending.Mask |= mask;
                    return;
                }
                if (pending.Kind == Kind.Fields)
                    Structural++;
                pending.Kind = kind;
                pending.Mask = mask;
                return;
            }
            if (Count == Pending.Length)
                Array.Resize(ref Pending, Math.Max(4, Count * 2));
            Pending[Count++] = new Pending { Component = component, Mask = mask, Kind = kind };
            if (kind != Kind.Fields)
                Structural++;
        }

        public void Clear()
        {
            Count = 0;
            Structural = 0;
            MotionSince = 0;
        }
    }
}
