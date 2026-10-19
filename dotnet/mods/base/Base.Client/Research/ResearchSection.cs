using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Раздел Research: окно исследований (gui/tech.ts, techOf). Открывается клавишей R (как в hud.ts) и по просьбе кнопки
/// верхней полосы: кнопка (раздел Hud, К3а) переключает <c>c.Shared["research.open"]</c>; закрывается по R, Esc и «✕».
/// Раздел отдаёт кнопке и ход ведущейся темы: <c>c.Shared["research.progress"]</c> — доля 0…1 или null, когда вести нечего.
/// </summary>
internal sealed class ResearchSection : Section
{
    /// <summary>Открыто ли окно: бул в c.Shared; верхняя полоса переключает его (кнопка «Исследования»).</summary>
    public const string OpenKey = "research.open";

    /// <summary>Доля готовности ведущейся темы (double 0…1) для подписи кнопки; null — вести нечего.</summary>
    public const string ProgressKey = "research.progress";

    /// <summary>Высота верхней полосы, под которой стоит окно: её публикует HUD (TopBar.TopKey), иначе — 38.</summary>
    private static float Top(ClientContext c) => c.Shared.TryGetValue(TopBar.TopKey, out var top) && top is float height ? height : TopBar.Height;

    private TechWindow _window = null!;

    public override void Setup(ClientBuilder client) => client.Ui("base.research", Frame);

    public override void Load(BaseState state)
    {
        base.Load(state);
        _window = new TechWindow();
    }

    public override void Frame(ClientContext c)
    {
        if (Profile?.Ui.Contains("hud") != true)
            return;
        var open = c.Shared.TryGetValue(OpenKey, out var value) && value is true;
        var input = c.Input;
        // Редактору карты окно не нужно (hud.ts); в меню клавиши молчат сами.
        var editor = (c.Shell & ShellMode.Editor) != 0;
        if (!editor && input.Hits.Contains("r") && !input.Ctrl && !input.Alt)
            open = !open;
        else if (open && input.Hits.Contains("escape"))
            open = false;
        if (editor)
            open = false;

        var tech = TechReader.Read(c, State);
        c.Shared[ProgressKey] = tech.Current is { } current ? tech.Topics.FirstOrDefault(view => view.Topic == current)?.Progress : null;
        if (open && _window.Draw(c, tech, Top(c)))
            open = false;
        c.Shared[OpenKey] = open;
    }
}
