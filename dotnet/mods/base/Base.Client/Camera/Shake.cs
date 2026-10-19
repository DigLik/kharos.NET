using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Тряска камеры от взрывов — перенос shake.ts. Сила копится от каждого толчка и понемногу уходит; камеру уводит на
/// квадрат силы: слабые толчки едва заметны, а сильные бьют. Движение плавное, по сумме синусов, а не случайные скачки.
/// </summary>
internal sealed class Shake
{
    /// <summary>Насколько далеко тряска уводит камеру при полной силе и обычном зуме, в тайлах мира.</summary>
    private const double MaxOffset = 0.25;

    /// <summary>
    /// Зум (пикселей на тайл), при котором сдвиг равен <see cref="MaxOffset"/>. На экране тряска растёт как квадрат зума:
    /// вдвое ближе — вчетверо сильнее, вдвое дальше — вчетверо слабее. Издалека взрыв почти не трясёт, вблизи — бьёт.
    /// </summary>
    private const double BaseZoom = 32;

    /// <summary>Сколько силы тряски уходит за секунду: от полной до нуля — за 1 / Decay секунды.</summary>
    private const double Decay = 2.8;

    /// <summary>Как быстро камера дёргается туда-сюда, раз в секунду.</summary>
    private const double Frequency = 40;

    private double _trauma, _time;

    /// <summary>Сила тряски сейчас, 0..1.</summary>
    public double Trauma => _trauma;

    /// <summary>Плавное число от -1 до 1, своё для каждого seed.</summary>
    private double Wave(double seed) =>
        (Math.Sin(_time * Frequency + seed) + Math.Sin(_time * Frequency * 1.73 + seed * 2.1) * 0.6 + Math.Sin(_time * Frequency * 2.9 + seed * 3.7) * 0.3) / 1.9;

    /// <summary>Толчок силой amount: 1 — самый сильный.</summary>
    public void Add(double amount) => _trauma = Math.Min(1, _trauma + amount);

    public void Update(double seconds)
    {
        _time += seconds;
        _trauma = Math.Max(0, _trauma - seconds * Decay);
    }

    /// <summary>Куда сейчас уведена камера при зуме zoom, в тайлах.</summary>
    public (double X, double Y) Offset(double zoom)
    {
        var strength = _trauma * _trauma * MaxOffset * (zoom / BaseZoom);
        return (Wave(0) * strength, Wave(17) * strength);
    }
}

/// <summary>
/// Что разделы камеры слушают в общей памяти (c.Shared): остальные разделы подачи не знают их устройства, а только
/// просят — толчок тряски, полёт к точке, прыжок по мини-карте. Просьба исполняется в следующем кадре (этап камеры идёт
/// раньше управления и эффектов). Скорость камеры над картой, тайлов в секунду, раздел кладёт в <see cref="SpeedKey"/>.
/// </summary>
internal static class CameraShake
{
    public const string ShakeKey = "camera.shake", FlyKey = "camera.flyTo", JumpKey = "camera.jump", SpeedKey = "camera.speed";

    /// <summary>Толчок тряски силой amount (1 — самый сильный); толчки за кадр складываются.</summary>
    public static void Add(ClientContext c, double amount) =>
        c.Shared[ShakeKey] = (c.Shared.TryGetValue(ShakeKey, out var held) && held is double sum ? sum : 0) + amount;

    /// <summary>Камера летит так, чтобы (x, y) оказалась в середине экрана (двойное нажатие цифры группы).</summary>
    public static void FlyTo(ClientContext c, double x, double y) => c.Shared[FlyKey] = (x, y);

    /// <summary>Камера сразу переходит в (x, y), без полёта и звука (щелчок по мини-карте).</summary>
    public static void Jump(ClientContext c, double x, double y) => c.Shared[JumpKey] = (x, y);

    /// <summary>Как быстро камера неслась над картой в прошлом кадре, тайлов в секунду.</summary>
    public static double Speed(ClientContext c) => c.Shared.TryGetValue(SpeedKey, out var value) && value is double speed ? speed : 0;
}
