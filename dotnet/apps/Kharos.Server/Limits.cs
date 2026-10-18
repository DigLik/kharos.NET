using System.Net;

namespace Kharos.Server;

/// <summary>Ведро жетонов (packages/host/src/limits.ts): в среднем rate в секунду, разом не больше burst. Не потокобезопасно.</summary>
public sealed class TokenBucket
{
    private readonly double _rate, _burst;
    private readonly Func<long> _now;
    private double _tokens;
    private long _last;

    /// <param name="now">Часы, мс.</param>
    public TokenBucket(double rate, double burst, Func<long> now)
    {
        _rate = rate;
        _burst = burst;
        _now = now;
        _tokens = burst;
        _last = now();
    }

    /// <summary>Берёт жетон; false — жетонов нет, действие пропускать нельзя.</summary>
    public bool Take()
    {
        var time = _now();
        _tokens = Math.Min(_burst, _tokens + (time - _last) / 1000.0 * _rate);
        _last = time;
        if (_tokens < 1)
            return false;
        _tokens--;
        return true;
    }
}

/// <summary>Ограничения на подключения с одного адреса; ноль (и меньше) снимает ограничение.</summary>
public sealed record ConnectLimits(int Connections = 8, int ConnectsPerMinute = 30, int NewPlayersPerHour = 5);

/// <summary>
/// Привратник сервера: считает подключения по адресам, чтобы один адрес не занял все места, не завалил сервер
/// попытками входа (заодно так не подобрать пароль) и не засеял карту новыми игроками. Потокобезопасен.
/// </summary>
public sealed class ConnectGate
{
    /// <summary>Больше скольких адресов привратник не помнит: дальше забывает те, с которых сейчас никто не подключён.</summary>
    private const int Remembered = 10000;

    private sealed class Entry(TokenBucket? attempts, TokenBucket? fresh)
    {
        public int Open;
        public readonly TokenBucket? Attempts = attempts, Fresh = fresh;
    }

    private readonly ConnectLimits _limits;
    private readonly Func<long> _now;
    private readonly Dictionary<string, Entry> _addresses = [];
    private readonly Lock _lock = new();

    public ConnectGate(ConnectLimits limits, Func<long>? now = null)
    {
        _limits = limits;
        _now = now ?? (() => Environment.TickCount64);
    }

    private Entry Of(string address)
    {
        if (_addresses.TryGetValue(address, out var entry))
            return entry;
        if (_addresses.Count >= Remembered)
            foreach (var key in _addresses.Where(pair => pair.Value.Open == 0).Select(pair => pair.Key).ToList())
                _addresses.Remove(key);
        entry = new Entry(
            _limits.ConnectsPerMinute > 0 ? new TokenBucket(_limits.ConnectsPerMinute / 60.0, _limits.ConnectsPerMinute, _now) : null,
            _limits.NewPlayersPerHour > 0 ? new TokenBucket(_limits.NewPlayersPerHour / 3600.0, _limits.NewPlayersPerHour, _now) : null);
        _addresses[address] = entry;
        return entry;
    }

    /// <summary>Попытка подключиться с адреса; false — слишком часто или уже слишком много подключений.</summary>
    public bool Admit(string address)
    {
        lock (_lock)
        {
            var entry = Of(address);
            return (entry.Attempts?.Take() ?? true) && (_limits.Connections <= 0 || entry.Open < _limits.Connections);
        }
    }

    /// <summary>Можно ли завести с адреса ещё одного нового игрока.</summary>
    public bool Newcomer(string address)
    {
        lock (_lock)
            return Of(address).Fresh?.Take() ?? true;
    }

    public void Opened(string address)
    {
        lock (_lock)
            Of(address).Open++;
    }

    public void Closed(string address)
    {
        lock (_lock)
            if (_addresses.TryGetValue(address, out var entry))
                entry.Open = Math.Max(0, entry.Open - 1);
    }
}

/// <summary>Адрес клиента за прокси.</summary>
public static class ClientAddress
{
    /// <summary>
    /// forwarded — заголовок X-Forwarded-For: ему верят, только когда подключился свой же прокси (direct — петля), и
    /// берут из него последний адрес — его дописал прокси, остальное мог прислать сам клиент.
    /// </summary>
    public static string Of(IPAddress? direct, string? forwarded)
    {
        var local = direct is null || IPAddress.IsLoopback(direct);
        var last = forwarded?.Split(',')[^1].Trim();
        if (local && !string.IsNullOrEmpty(last))
            return last;
        if (direct is null)
            return "unknown";
        return (direct.IsIPv4MappedToIPv6 ? direct.MapToIPv4() : direct).ToString();
    }
}
