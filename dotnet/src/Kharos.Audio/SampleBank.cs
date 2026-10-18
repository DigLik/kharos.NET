namespace Kharos.Audio;

/// <summary>Запись: каналы float (1 или 2; у отклика эха — 1, 2 или 4) одной длины. Неизменна после создания.</summary>
public sealed class SampleBuffer
{
    private readonly float[][] _channels;

    public SampleBuffer(IReadOnlyList<float[]> channels, int sampleRate = AudioFormat.SampleRate)
    {
        if (channels.Count is 0 or > 4 || channels.Count == 3)
            throw new ArgumentException("в записи 1, 2 или 4 канала", nameof(channels));
        if (sampleRate is < 3000 or > 768000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        var length = channels[0].Length;
        foreach (var channel in channels)
            if (channel.Length != length)
                throw new ArgumentException("каналы записи разной длины", nameof(channels));
        _channels = [.. channels.Select(channel => (float[])channel.Clone())];
        SampleRate = sampleRate;
        Length = length;
    }

    /// <summary>Моно-запись.</summary>
    public SampleBuffer(float[] mono, int sampleRate = AudioFormat.SampleRate) : this([mono], sampleRate)
    {
    }

    public int ChannelCount => _channels.Length;

    /// <summary>Длина в кадрах.</summary>
    public int Length { get; }

    public int SampleRate { get; }

    /// <summary>Длительность, секунды.</summary>
    public double Duration => Length / (double)SampleRate;

    /// <summary>Канал записи (только чтение).</summary>
    public ReadOnlySpan<float> Channel(int index) => _channels[index];

    internal float[] Data(int index) => _channels[index];
}

/// <summary>Запись банка: варианты и шина.</summary>
public sealed class SampleRecord(IReadOnlyList<SampleBuffer> variants, Bus bus)
{
    public IReadOnlyList<SampleBuffer> Variants { get; } = variants.Count > 0 ? [.. variants] : throw new ArgumentException("нет вариантов", nameof(variants));

    public Bus Bus { get; } = bus;
}

/// <summary>
/// Банк записей по именам (как bank пилота: «motor:petrol:idle», «reverb», «noise:pink»). Пишется и читается в потоке
/// игры: аудиопоток получает готовые ссылки на записи.
/// </summary>
public sealed class SampleBank
{
    private readonly Dictionary<string, SampleRecord> _records = new(StringComparer.Ordinal);

    public int Count => _records.Count;

    public IEnumerable<string> Names => _records.Keys;

    /// <summary>Добавить или заменить запись.</summary>
    public void Add(string name, SampleRecord record) => _records[name] = record;

    /// <summary>Добавить запись с вариантами.</summary>
    public void Add(string name, Bus bus, params SampleBuffer[] variants) => Add(name, new SampleRecord(variants, bus));

    public bool TryGet(string name, out SampleRecord record) => _records.TryGetValue(name, out record!);

    public bool Contains(string name) => _records.ContainsKey(name);
}
