using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk.Client;

/// <summary>
/// Мод на клиенте (подача, docs/dotnet.md Р11): облики видов, звуки, интерфейс, камера, управление, эффекты,
/// атмосфера, музыка. Мир он видит только на чтение (<see cref="ClientContext"/>), менять может только приказом
/// (<see cref="ClientContext.Send(string, ReadOnlySpan{byte})"/>). Свою память — выделение, камеру, открытые панели — держит в
/// полях: экземпляр клиентского мода живёт всю партию. Класс называется в проекте *.Client свойством
/// <c>KharosModClass</c>: экспорты ABI SDK пишет сам.
/// </summary>
public abstract class ClientMod
{
    /// <summary>Объявления: облики, звуки, обработчики кадра. Не зависит от данных.</summary>
    public abstract void Setup(ClientBuilder client);

    /// <summary>Данные мода и схема мира — раз на экземпляр, после <see cref="Setup"/>.</summary>
    public virtual void Load(LoadContext ctx)
    {
    }
}

/// <summary>Обработчик кадра клиента.</summary>
public delegate void FrameHandler(ClientContext c);

/// <summary>Обработчик сигнала хоста.</summary>
public delegate void SignalHandler(ClientContext c, HostSignal signal);

/// <summary>
/// Объявления клиентского мода. Обработчики кадра идут в порядке пилота (docs/mods.md, «Клиент»): сигналы хоста →
/// камера → панели → управление → эффекты → атмосфера → звук → музыка; внутри группы — в порядке объявления.
/// </summary>
public sealed class ClientBuilder
{
    internal enum Stage { Camera, Ui, Controls, Effects, Atmosphere, Audio, Music, Count }

    internal List<(string Proto, LookSpec Spec)> Looks { get; } = [];
    internal List<(ModBankInfo Info, BankRender Render)> Bank { get; } = [];
    internal List<ModVoiceInfo> Voices { get; } = [];
    internal Dictionary<string, List<SignalHandler>> Signals { get; } = new(StringComparer.Ordinal);
    internal List<(string Name, FrameHandler Handler)>[] Stages { get; } = [.. Enumerable.Range(0, (int)Stage.Count).Select(_ => new List<(string, FrameHandler)>())];
    internal uint[] MinimapPalette { get; private set; } = [];

    /// <summary>Облик вида proto («раздел/имя»).</summary>
    public void Look(string proto, LookSpec spec)
    {
        ArgumentException.ThrowIfNullOrEmpty(proto);
        ArgumentNullException.ThrowIfNull(spec);
        Looks.Add((proto, spec));
    }

    /// <summary>Запись банка звуков: render считает каналы при загрузке, на каждый вариант.</summary>
    public void Sample(string name, Bus bus, int variants, BankRender render)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(variants, 1);
        Bank.Add((new ModBankInfo { Name = name, Bus = bus switch { Bus.Battle => "battle", Bus.Interface => "interface", Bus.Music => "music", _ => "world" }, Variants = variants }, render));
    }

    /// <summary>Голос по имени: готовый граф (c.audio:voice по виду).</summary>
    public void Voice(string name, VoiceGraph graph)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Voices.Add(new ModVoiceInfo { Name = name, Graph = graph.Bytes });
    }

    /// <summary>Цвета мини-карты 0xRRGGBB: 4 биома × 4 вида местности (болото, песок, скала, гора), биом за биомом.</summary>
    public void Minimap(params uint[] palette)
    {
        if (palette.Length != 16)
            throw new ArgumentException("мини-карта — 16 цветов", nameof(palette));
        MinimapPalette = palette;
    }

    /// <summary>Сигнал хоста name (ctx.Commands.Signal на хосте) — до камеры.</summary>
    public void Signal(string name, SignalHandler handler)
    {
        if (!Signals.TryGetValue(name, out var list))
            Signals[name] = list = [];
        list.Add(handler);
    }

    public void Camera(string name, FrameHandler handler) => Stages[(int)Stage.Camera].Add((name, handler));
    public void Ui(string name, FrameHandler handler) => Stages[(int)Stage.Ui].Add((name, handler));
    public void Controls(string name, FrameHandler handler) => Stages[(int)Stage.Controls].Add((name, handler));
    public void Effects(string name, FrameHandler handler) => Stages[(int)Stage.Effects].Add((name, handler));
    public void Atmosphere(string name, FrameHandler handler) => Stages[(int)Stage.Atmosphere].Add((name, handler));
    public void Audio(string name, FrameHandler handler) => Stages[(int)Stage.Audio].Add((name, handler));
    public void Music(string name, FrameHandler handler) => Stages[(int)Stage.Music].Add((name, handler));

    internal ModManifest ToManifest() => new()
    {
        Abi = ModAbi.Version,
        Looks = [.. Looks.Select(look => look.Proto)],
        Bank = [.. Bank.Select(entry => entry.Info)],
        Voices = [.. Voices],
        Minimap = MinimapPalette,
    };
}
