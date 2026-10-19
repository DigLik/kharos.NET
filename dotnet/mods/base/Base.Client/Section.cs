using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Раздел клиентской части base (docs/dotnet-n6.md, Н6.5): папка &lt;Раздел&gt;/ с файлом &lt;Раздел&gt;Section.cs. Setup
/// объявляет облики, звуки, сигналы и обработчики этапов кадра (обычно — <see cref="Frame"/> на своём этапе: Camera,
/// Ui, Controls, Effects, Atmosphere, Audio, Music); Load получает данные и компоненты партии; Frame — работа раздела
/// за кадр. Порядок разделов — <see cref="BaseClient"/>; внутри этапа обработчики идут в этом порядке.
/// </summary>
internal abstract class Section
{
    /// <summary>Общее разделов: данные, компоненты, подача партии; есть после <see cref="Load"/>.</summary>
    protected BaseState State { get; private set; } = null!;

    /// <summary>Подача партии (games.json, client); у чужой партии — null, и разделы подачи молчат.</summary>
    protected Base.Shared.Data.ClientSpec? Profile => State.Profile;

    public abstract void Setup(ClientBuilder client);

    public virtual void Load(BaseState state) => State = state;

    public abstract void Frame(ClientContext c);
}
