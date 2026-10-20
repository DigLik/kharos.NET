using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Base.Shared.Orders;
using Kharos.Client.Game;
using Kharos.Client.Net;
using Kharos.Engine.World;
using Kharos.Protocol;
using MemoryPack;

namespace Kharos.Smoke;

/// <summary>Итог нагрузки и ворота Н7.3: нет обрывов, худший тик ≤ 50 мс, память сервера не растёт после разгона.</summary>
internal sealed record SoakResult
{
    public int Bots { get; init; }
    public double Minutes { get; init; }
    public int Connected { get; init; }
    public int Disconnects { get; init; }
    public List<string> DisconnectNotes { get; init; } = [];
    public int Orders { get; init; }
    public int Spawns { get; init; }
    public int Kills { get; init; }
    public int MaxEntities { get; init; }

    /// <summary>Строк статистики хоста (раз в 5 с) за время после входа всех ботов.</summary>
    public int Reports { get; init; }
    public double WorstTickMs { get; init; }
    public double MeanTickMs { get; init; }

    /// <summary>Худший тик за весь прогон, вместе со входом ботов (снимки мира новичкам).</summary>
    public double WorstTickAllMs { get; init; }
    public double WarmupMinutes { get; init; }
    public string MemorySource { get; init; } = "";
    public double MemoryStartMb { get; init; }
    public double MemoryEndMb { get; init; }
    public double MemoryGrowthPercent { get; init; }
    public List<double[]> Memory { get; init; } = [];
    public List<string> Failures { get; init; } = [];
    public bool Passed => Failures.Count == 0;

    public const double WorstTickLimit = 50, MemoryGrowthLimit = 10;

    public string Describe() =>
        $"ботов {Connected}/{Bots}, {Minutes:0.0} мин, обрывов {Disconnects}, приказов {Orders}, /spawn {Spawns}, потерь юнитов {Kills}, сущностей до {MaxEntities}; " +
        $"тик среднее {MeanTickMs:0.00} мс, худший {WorstTickMs:0.00} мс (со входом ботов {WorstTickAllMs:0.00}), строк {Reports}; " +
        $"память ({MemorySource}) после {WarmupMinutes:0.#} мин: {MemoryStartMb:0} → {MemoryEndMb:0} МБ ({MemoryGrowthPercent:+0.0;-0.0} %)" +
        (Failures.Count > 0 ? "; ПРОВАЛ: " + string.Join("; ", Failures) : "");
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SoakResult))]
internal sealed partial class SoakJson : JsonSerializerContext;

/// <summary>
/// Нагрузка (Н7.3): боты — клиенты протокола без GPU (<see cref="GameConnector"/> и реплика). Каждый входит под своим ником,
/// включает режим администратора (/admin smoke, ради /spawn), разворачивает MCV, раз в пару секунд шлёт
/// своим юнитам атаку на ближайшего видимого чужого, приказ точкой к армии другого бота (сходятся и воюют) или поездку
/// рядом; подкрепления — /spawn. Сервер ведёт журнал
/// статистики (раз в 5 с: тик среднее и худший, память); память локального сервера — ещё и замером процесса.
/// </summary>
internal sealed partial class Soak(string url, int bots, TimeSpan duration, Process? serverProcess, Proc serverLog, Action<string> log, string? cacheDir = null)
{
    [GeneratedRegex(@"тик (?<avg>[\d.]+) мс, худший (?<worst>[\d.]+) мс, память (?<mem>\d+) МБ")]
    private static partial Regex StatsLine();

    private sealed record Stats(double At, double Avg, double Worst, double Memory);

    public async Task<SoakResult> RunAsync(CancellationToken cancel)
    {
        var clock = Stopwatch.StartNew();
        var stats = new List<Stats>();
        var statsGate = new Lock();
        void OnLine(string line)
        {
            var match = StatsLine().Match(line);
            if (!match.Success)
                return;
            lock (statsGate)
                stats.Add(new Stats(clock.Elapsed.TotalSeconds, Num(match, "avg"), Num(match, "worst"), Num(match, "mem")));
        }
        serverLog.Line += OnLine;
        var crew = new List<Bot>();
        var failures = new List<string>();
        var memory = new List<double[]>();
        double joined = 0;
        try
        {
            for (var i = 1; i <= bots; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var bot = new Bot($"bot{i}", i, crew, cacheDir);
                try
                {
                    await bot.ConnectAsync(url, cancel).ConfigureAwait(false);
                    crew.Add(bot);
                    log($"       {bot.Name}: игрок {bot.Player}, сущностей видно {bot.Count}");
                }
                catch (Exception error) when (error is TransportException or ClientException or IOException or TimeoutException)
                {
                    failures.Add($"{bot.Name} не вошёл: {error.Message}");
                }
            }
            joined = clock.Elapsed.TotalSeconds;
            var end = clock.Elapsed + duration;
            var nextSample = TimeSpan.Zero;
            var nextNote = clock.Elapsed + TimeSpan.FromMinutes(1);
            while (clock.Elapsed < end && !cancel.IsCancellationRequested)
            {
                foreach (var bot in crew)
                    bot.Tick(clock.Elapsed.TotalSeconds);
                if (clock.Elapsed >= nextSample)
                {
                    nextSample = clock.Elapsed + TimeSpan.FromSeconds(5);
                    if (serverProcess is { HasExited: false })
                    {
                        serverProcess.Refresh();
                        memory.Add([Math.Round(clock.Elapsed.TotalSeconds - joined, 1), Math.Round(serverProcess.WorkingSet64 / 1048576.0, 1)]);
                    }
                }
                if (clock.Elapsed >= nextNote)
                {
                    nextNote = clock.Elapsed + TimeSpan.FromMinutes(1);
                    Stats? last;
                    lock (statsGate)
                        last = stats.LastOrDefault();
                    log($"       {(clock.Elapsed.TotalSeconds - joined) / 60:0} мин: ботов на связи {crew.Count(b => !b.Lost)}, приказов {crew.Sum(b => b.Orders)}, сущностей {crew.Max(b => b.Count)}" +
                        (last is null ? "" : $", тик {last.Avg:0.00}/{last.Worst:0.00} мс, память {last.Memory:0} МБ"));
                }
                if (serverLog.Exited)
                {
                    failures.Add($"сервер вышел посреди нагрузки (код {serverLog.ExitCode})");
                    break;
                }
                await Task.Delay(50, cancel).ConfigureAwait(false);
            }
        }
        finally
        {
            serverLog.Line -= OnLine;
            foreach (var bot in crew)
                bot.Dispose();
        }
        cancel.ThrowIfCancellationRequested();

        List<Stats> after;
        double worstAll;
        lock (statsGate)
        {
            // Строка за интервал, в который ещё входили боты, в ворота не идёт (снимок мира новичку — не тик игры).
            after = [.. stats.Where(s => s.At >= joined + 5.5)];
            worstAll = stats.Count > 0 ? stats.Max(s => s.Worst) : 0;
        }
        var minutes = (clock.Elapsed.TotalSeconds - joined) / 60;
        var warmup = Math.Min(5, minutes / 3);
        var source = "процесс";
        if (memory.Count == 0)
        {
            source = "журнал сервера";
            memory = [.. after.Select(s => new[] { Math.Round(s.At - joined, 1), s.Memory })];
        }
        var window = memory.Where(m => m[0] >= warmup * 60).Select(m => m[1]).ToList();
        double start = 0, finish = 0, growth = 0;
        if (window.Count >= 4)
        {
            var fifth = Math.Max(1, window.Count / 5);
            start = Median(window.Take(fifth));
            finish = Median(window.Skip(window.Count - fifth));
            growth = start > 0 ? (finish - start) / start * 100 : 0;
        }
        else
            failures.Add($"мало замеров памяти после разгона ({window.Count})");
        var disconnects = crew.Where(b => b.Lost).ToList();
        foreach (var bot in disconnects)
            failures.Add($"обрыв {bot.Name}: {bot.LostReason}");
        if (after.Count == 0)
            failures.Add("нет строк статистики хоста в журнале сервера");
        var worst = after.Count > 0 ? after.Max(s => s.Worst) : 0;
        if (worst > SoakResult.WorstTickLimit)
            failures.Add($"худший тик {worst:0.00} мс > {SoakResult.WorstTickLimit} мс");
        if (growth > SoakResult.MemoryGrowthLimit)
            failures.Add($"память выросла на {growth:0.0} % > {SoakResult.MemoryGrowthLimit} %");
        return new SoakResult
        {
            Bots = bots,
            Minutes = Math.Round(minutes, 2),
            Connected = crew.Count,
            Disconnects = disconnects.Count,
            DisconnectNotes = [.. disconnects.Select(b => $"{b.Name}: {b.LostReason}")],
            Orders = crew.Sum(b => b.Orders),
            Spawns = crew.Sum(b => b.Spawns),
            Kills = crew.Sum(b => b.Losses),
            MaxEntities = crew.Count > 0 ? crew.Max(b => b.MaxCount) : 0,
            Reports = after.Count,
            WorstTickMs = worst,
            MeanTickMs = after.Count > 0 ? Math.Round(after.Average(s => s.Avg), 2) : 0,
            WorstTickAllMs = worstAll,
            WarmupMinutes = Math.Round(warmup, 2),
            MemorySource = source,
            MemoryStartMb = start,
            MemoryEndMb = finish,
            MemoryGrowthPercent = Math.Round(growth, 2),
            Memory = memory,
            Failures = failures,
        };
    }

    private static double Num(Match match, string group) => double.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }
}

/// <summary>Бот нагрузки: соединение, реплика и простое поведение.</summary>
internal sealed class Bot(string name, int seed, IReadOnlyList<Bot> crew, string? cacheDir = null) : IDisposable
{
    /// <summary>Ниже стольких своих юнитов бот просит подкрепление /spawn.</summary>
    private const int Few = 6;

    private static readonly HashSet<string> Unarmed = ["mcv", "builder", "truck", "airTruck", "supplier"];
    private readonly Random _random = new(seed * 7919);
    private readonly List<ReplicaSignal> _signals = [];
    private WebSocketTransport? _transport;
    private GameConnector? _connector;
    private Dictionary<int, Kharos.Content.Proto> _protos = [];
    private double _nextOrder, _nextSpawn;
    private bool _deployed;
    private int _lastOwn = -1;

    public string Name { get; } = name;

    /// <summary>Где армия бота (среднее своих юнитов); другие боты едут сюда: чужое далеко за туманом, а ехать надо.</summary>
    public (double X, double Y)? Center { get; private set; }
    public uint Player => _connector?.Replica.Player ?? 0;
    public int Count => _connector?.Replica.World?.Count ?? 0;
    public int MaxCount { get; private set; }
    public int Orders { get; private set; }
    public int Spawns { get; private set; }
    public int Losses { get; private set; }
    public bool Lost { get; private set; }
    public string? LostReason { get; private set; }

    public async Task ConnectAsync(string url, CancellationToken cancel)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var address = WebSocketTransport.WithQuery(new Uri(url), [
            new("id", token), new("name", Name), new("password", ""),
            new("version", Welcome.ProtocolVersion.ToString(CultureInfo.InvariantCulture)),
        ]);
        _transport = await WebSocketTransport.ConnectAsync(address, cancel: cancel).ConfigureAwait(false);
        var transport = _transport;
        // Моды сервера — в общий кэш ботов: качает первый, остальные берут оттуда (как игрок, который уже заходил).
        var cache = cacheDir is null ? null : new ContentCache(cacheDir);
        _connector = await Task.Run(() => GameConnector.Connect(transport, cache, TimeSpan.FromMinutes(3)), cancel).ConfigureAwait(false);
        _protos = _connector.Content!.Protos.ToDictionary(p => p.Id);
        // /admin — ради /spawn. Карту не открываем (/reveal): чужие юниты всё равно видны только своим обзором.
        Say("/admin smoke");
    }

    /// <summary>Строки чата уходят не чаще раза в 0,6 с: хост молча выбрасывает чат чаще ChatGap (0,5 с).</summary>
    private readonly Queue<string> _chat = new();
    private double _nextChat;

    private void Say(string text) => _chat.Enqueue(text);

    private void FlushChat(double now)
    {
        if (_chat.Count == 0 || now < _nextChat)
            return;
        _transport!.Send(Messages.Say(_chat.Dequeue()));
        _nextChat = now + 0.6;
    }

    private void Send<T>(string order, T payload)
    {
        _transport!.Send(DeltaCodec.Order(order, MemoryPackSerializer.Serialize(payload)));
        Orders++;
    }

    public void Tick(double now)
    {
        if (Lost || _connector is null || _transport is null)
            return;
        if (_transport.Closed)
        {
            Lost = true;
            LostReason = _transport.CloseReason ?? _transport.Error?.Message ?? "соединение закрыто";
            return;
        }
        try
        {
            _connector.Poll();
        }
        catch (FormatException error)
        {
            Lost = true;
            LostReason = $"сообщение не разобрано: {error.Message}";
            return;
        }
        FlushChat(now);
        var replica = _connector.Replica;
        replica.TakeSignals(_signals);
        _signals.Clear();
        replica.Inbox.Clear();
        replica.Changes.Clear();
        MaxCount = Math.Max(MaxCount, Count);
        if (now < _nextOrder || replica.World is not { } world)
            return;
        _nextOrder = now + 1.5 + _random.NextDouble();
        Act(world, replica.Player, now);
    }

    private void Act(GameWorld world, uint me, double now)
    {
        var store = world.Store(Builtin.Proto);
        var ids = store.Column(0);
        var entities = store.Entities;
        var own = new List<(uint Entity, string Name, double X, double Y)>();
        var foes = new List<(uint Entity, double X, double Y)>();
        for (var row = 0; row < entities.Length; row++)
        {
            var entity = entities[row];
            if (!_protos.TryGetValue((int)ids[row], out var proto) || proto.Section is not ("units" or "buildings"))
                continue;
            if (!world.TryNumber(entity, Builtin.Owner, 0, out var owner) || owner == 0)
                continue;
            if (!world.TryNumber(entity, Builtin.Position, 0, out var x) || !world.TryNumber(entity, Builtin.Position, 1, out var y))
                continue;
            if ((uint)owner == me)
            {
                if (proto.Section == "units")
                    own.Add((entity, proto.Name, x, y));
            }
            else
                foes.Add((entity, x, y));
        }
        if (_lastOwn > own.Count)
            Losses += _lastOwn - own.Count;
        _lastOwn = own.Count;
        if (!_deployed && own.FirstOrDefault(u => u.Name == "mcv") is { Entity: not 0 } mcv)
        {
            Send(DeployOrder.Name, new DeployOrder { Unit = mcv.Entity });
            _deployed = true;
        }
        var army = own.Where(u => !Unarmed.Contains(u.Name)).ToList();
        if (army.Count < Few && now >= _nextSpawn)
        {
            Say("/spawn buggy 4");
            Spawns++;
            _nextSpawn = now + 15;
        }
        if (army.Count == 0)
            return;
        var (cx, cy) = (army.Average(u => u.X), army.Average(u => u.Y));
        Center = (cx, cy);
        var others = crew.Where(b => b != this && !b.Lost && b.Center is not null).ToList();
        var roll = _random.NextDouble();
        // Видно чужого — в бой на ближайшего; нет — приказ точкой к армии другого бота (мод решает: ехать или атаковать);
        // иногда — прокатиться рядом (ездят и воюют).
        if (foes.Count > 0 && roll < 0.67)
        {
            var target = foes.MinBy(f => (f.X - cx) * (f.X - cx) + (f.Y - cy) * (f.Y - cy));
            Send(AttackOrder.Name, new AttackOrder { Units = [.. army.Select(u => u.Entity)], Target = target.Entity });
        }
        else if (foes.Count == 0 && others.Count > 0 && roll < 0.8)
        {
            var (tx, ty) = others[_random.Next(others.Count)].Center!.Value;
            Send(PointOrder.Name, new PointOrder { Units = [.. army.Select(u => u.Entity)], X = tx, Y = ty });
        }
        else
        {
            var half = army.Where((_, i) => i % 2 == 0).Select(u => u.Entity).ToArray();
            Send(MoveOrder.Name, new MoveOrder { Units = half, X = (int)(cx + _random.Next(-24, 25)), Y = (int)(cy + _random.Next(-24, 25)) });
        }
    }

    public void Dispose() => (_transport as IDisposable)?.Dispose();
}
