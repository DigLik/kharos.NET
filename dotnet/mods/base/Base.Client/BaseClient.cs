using System.Text.Json;
using Base.Client.Sounds;
using Base.Shared.Data;
using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Клиентская часть base (docs/dotnet.md, Р11; docs/dotnet-n6.md, Н6.5): облики, эффекты, камера, управление,
/// атмосфера, панели, звук и музыка — по разделам &lt;Раздел&gt;/&lt;Раздел&gt;Section.cs. Здесь только список разделов в
/// неизменном порядке; файл заморожен, как BaseMod.cs у хоста. Данные — data/*.json при загрузке, подача партии —
/// подобъект <c>client</c> записи data/games.json (<see cref="ClientSpec"/>).
/// </summary>
public sealed class BaseClient : ClientMod
{
    // Порядок — порядок объявлений и обработчиков внутри этапа кадра (этапы задаёт движок: камера → панели → управление →
    // эффекты → атмосфера → звук → музыка).
    private readonly Section[] _sections =
    [
        new ArtSection(),
        new SoundsSection(),
        new CameraSection(),
        new HudSection(),
        // Как в gui/hud.ts: уведомления и «Первые шаги» — под окнами (список игроков, поражение, исследования).
        new AlertsSection(),
        new StepsSection(),
        new PanelsSection(),
        new ResearchSection(),
        new EditorSection(),
        new ControlsSection(),
        new EffectsSection(),
        new AtmosphereSection(),
        new AudioSection(),
        new MusicSection(),
    ];

    public override void Setup(ClientBuilder client)
    {
        foreach (var section in _sections)
            section.Setup(client);
    }

    public override void Load(LoadContext ctx)
    {
        var state = new BaseState(ClientData.Load(ctx), new Parts(ctx), ProfileOf(ctx));
        foreach (var section in _sections)
            section.Load(state);
    }

    /// <summary>Подача идущей партии: games.json → запись партии → client; нет — null (подачи base нет).</summary>
    private static ClientSpec? ProfileOf(LoadContext ctx) =>
        ctx.TryData("games", out var bytes) && JsonSerializer.Deserialize(bytes, BaseJson.Default.DictionaryStringGameSpec) is { } games
            ? games.GetValueOrDefault(ctx.Game)?.Client
            : null;
}
