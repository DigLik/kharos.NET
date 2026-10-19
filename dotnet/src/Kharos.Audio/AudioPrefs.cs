using System.Globalization;
using System.Text;

namespace Kharos.Audio;

/// <summary>
/// Громкости игрока в хранилище клиента (IStorage) под ключами движка «engine/audio/…»: общая (volume), выключение (muted) и по
/// шине (level/&lt;шина&gt;), как kharos.volume, kharos.muted и kharos.level.* в TS. Значения — текст (инвариантная
/// культура). Изменение сразу уходит в звук и в хранилище.
/// </summary>
public sealed class AudioPrefs
{
    /// <summary>Доля громкости, пока поверх игры открыто меню.</summary>
    public const float Dimmed = 0.25f;

    public const string VolumeKey = "engine/audio/volume";
    public const string MutedKey = "engine/audio/muted";
    public static string LevelKey(Bus bus) => "engine/audio/level/" + Buses.Name(bus);

    private readonly AudioEngine? _audio;
    private readonly Func<string, byte[]?> _get;
    private readonly Action<string, byte[]> _set;
    private readonly float[] _levels = new float[Buses.Count];
    private float _volume;
    private bool _muted;
    private bool _dimmed;

    /// <param name="get">Байты ключа хранилища клиента (IStorage.Get); нет — null.</param>
    /// <param name="set">Запомнить байты ключа (IStorage.Set).</param>
    public AudioPrefs(AudioEngine? audio, Func<string, byte[]?> get, Action<string, byte[]> set)
    {
        _audio = audio;
        _get = get;
        _set = set;
        Refresh();
    }

    /// <summary>Общая громкость игрока, 0..1.</summary>
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(float.IsFinite(value) ? value : 1f, 0f, 1f);
            Store(VolumeKey, _volume.ToString("R", CultureInfo.InvariantCulture));
            ApplyMaster();
        }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            _muted = value;
            Store(MutedKey, value ? "1" : "0");
            ApplyMaster();
        }
    }

    /// <summary>Приглушено: открыто меню поверх игры. Не запоминается.</summary>
    public bool Dim
    {
        get => _dimmed;
        set
        {
            _dimmed = value;
            ApplyMaster();
        }
    }

    /// <summary>Громкость шины, 0..1.</summary>
    public float Level(Bus bus) => _levels[(int)bus];

    public void SetLevel(Bus bus, float value)
    {
        value = Math.Clamp(float.IsFinite(value) ? value : 1f, 0f, 1f);
        _levels[(int)bus] = value;
        Store(LevelKey(bus), value.ToString("R", CultureInfo.InvariantCulture));
        _audio?.SetVolume(bus, value);
    }

    /// <summary>Перечитать хранилище и применить (настройки поменяли снаружи).</summary>
    public void Refresh()
    {
        _volume = ReadFloat(VolumeKey);
        _muted = _get(MutedKey) is { } muted && Encoding.UTF8.GetString(muted) == "1";
        for (var i = 0; i < Buses.Count; i++)
        {
            _levels[i] = ReadFloat(LevelKey((Bus)i));
            _audio?.SetVolume((Bus)i, _levels[i]);
        }
        ApplyMaster();
    }

    /// <summary>Общая громкость в движок: выключено — 0, приглушено — четверть.</summary>
    private void ApplyMaster() => _audio?.SetMasterVolume(_muted ? 0f : _volume * (_dimmed ? Dimmed : 1f));

    private float ReadFloat(string key) =>
        _get(key) is { } bytes && float.TryParse(Encoding.UTF8.GetString(bytes), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v)
            ? Math.Clamp(v, 0f, 1f)
            : 1f;

    private void Store(string key, string value) => _set(key, Encoding.UTF8.GetBytes(value));
}
