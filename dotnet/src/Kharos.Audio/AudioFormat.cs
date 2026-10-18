namespace Kharos.Audio;

/// <summary>Формат движка звука: 48 кГц, стерео float, обработка квантами по 128 кадров, как в Web Audio.</summary>
public static class AudioFormat
{
    /// <summary>Частота дискретизации, Гц.</summary>
    public const int SampleRate = 48000;

    /// <summary>Квант обработки (render quantum) в кадрах.</summary>
    public const int Quantum = 128;

    /// <summary>Каналов на выходе.</summary>
    public const int Channels = 2;

    /// <summary>Частота Найквиста.</summary>
    public const float Nyquist = SampleRate / 2f;
}

/// <summary>Шины громкости (группы CHANNELS в audio.ts): все сходятся в общую громкость и компрессор.</summary>
public enum Bus : byte
{
    Battle = 0,
    World = 1,
    Interface = 2,
    Music = 3,
}

/// <summary>Имена шин, как их пишут моды.</summary>
public static class Buses
{
    /// <summary>Сколько шин.</summary>
    public const int Count = 4;

    /// <summary>Шина по имени: battle, world, interface, music; иначе <c>null</c>.</summary>
    public static Bus? Parse(string? name) => name switch
    {
        "battle" => Bus.Battle,
        "world" => Bus.World,
        "interface" => Bus.Interface,
        "music" => Bus.Music,
        _ => null,
    };

    /// <summary>Имя шины.</summary>
    public static string Name(Bus bus) => bus switch
    {
        Bus.Battle => "battle",
        Bus.World => "world",
        Bus.Interface => "interface",
        _ => "music",
    };
}
