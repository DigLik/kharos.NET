using Kharos.Audio;
using Kharos.Client.Ui;
using Kharos.I18n;

namespace Kharos.Client.Shell;

/// <summary>Строки оболочки на языке игрока.</summary>
public sealed class Words(Texts texts, Func<string> lang)
{
    public Texts Texts { get; } = texts;
    public string Lang => lang();
    public string this[string key] => Texts.T(Lang, key);
    public string T(string key, params ReadOnlySpan<(string Name, object Value)> args) => Texts.T(Lang, key, args);
}

/// <summary>
/// Окно настроек игрока (settings.ts): язык, звук (выключить, общая громкость, шины), экран (полный экран,
/// вертикальная синхронизация, масштаб интерфейса, предел кадров), клавиши оболочки. Одно и в главном меню, и в меню
/// поверх игры; смена видна сразу.
/// </summary>
public sealed class SettingsWindow(ShellPrefs prefs, AudioPrefs audio, Words words)
{
    public const float Width = 300;
    private const float Label = 88;
    private const float Inner = Width - 36;

    private static readonly Bus[] Channels = [Bus.Music, Bus.Battle, Bus.World, Bus.Interface];

    /// <summary>Какое действие ждёт новую клавишу; null — никакое.</summary>
    public ShellKey? Binding { get; private set; }

    /// <summary>Полный экран переключили в окне: оболочке — переключить окно.</summary>
    public Action<bool>? OnFullscreen { get; set; }

    /// <summary>Окно; true — нажали «Назад». hits — клавиши кадра (для перевешивания).</summary>
    public bool Draw(Forms forms, IReadOnlyList<string> hits, Anchor? anchor = null, bool modal = false)
    {
        var wasBinding = Binding is not null;
        if (Binding is { } binding)
        {
            foreach (var hit in hits)
            {
                if (hit != "escape")
                    prefs.SetKey(binding, hit);
                Binding = null;
                break;
            }
        }
        var back = forms.Window("settings", words["common.settings"], Width, () =>
        {
            forms.GroupTitle(words["menu.language"], first: true);
            var langs = Langs.All;
            var current = langs.ToList().FindIndex(pair => pair.Lang == prefs.Lang);
            var picked = forms.Chips("settings:lang", [.. langs.Select(pair => pair.Name)], current, Inner);
            if (picked >= 0)
                prefs.SetLang(langs[picked].Lang);

            forms.GroupTitle(words["menu.sound"]);
            var sound = forms.Checkbox("settings:muted", !audio.Muted, words["menu.soundAndMusic"]);
            if (sound == audio.Muted)
                audio.Muted = !sound;
            Volume("settings:volume", words["menu.master"], audio.Volume, value => audio.Volume = value);
            foreach (var bus in Channels)
                Volume($"settings:level:{Buses.Name(bus)}", words[$"menu.channels.{Buses.Name(bus)}"], audio.Level(bus), value => audio.SetLevel(bus, value));

            forms.GroupTitle(words["settings.video"]);
            var full = forms.Checkbox("settings:fullscreen", prefs.Fullscreen, words["settings.fullscreen"]);
            if (full != prefs.Fullscreen)
            {
                prefs.SetFullscreen(full);
                OnFullscreen?.Invoke(full);
            }
            var vsync = forms.Checkbox("settings:vsync", prefs.VSync, words["settings.vsync"]);
            if (vsync != prefs.VSync)
                prefs.SetVSync(vsync);
            forms.Gui.Text(words["settings.uiScale"], new TextStyle { Color = Colors.Dim });
            var scales = ShellPrefs.Scales;
            var scale = forms.Chips("settings:scale", [.. scales.Select(s => s == 0 ? words["settings.auto"] : ShellPrefs.Percent(s))], Array.IndexOf(scales, prefs.Scale), Inner, fill: true);
            if (scale >= 0)
                prefs.SetScale(scales[scale]);
            forms.Gui.Box(new Style { Id = "settings:fps:label" }, () => forms.Gui.Text(words["menu.fps"], new TextStyle { Color = Colors.Dim }));
            forms.Gui.Tip("settings:fps:label", words["menu.fpsTip"]);
            var limits = ShellPrefs.FpsLimits;
            var fps = forms.Chips("settings:fps", [.. limits.Select(n => n == 0 ? words["menu.noLimit"] : n.ToString(System.Globalization.CultureInfo.InvariantCulture))], Array.IndexOf(limits, prefs.FpsLimit), Inner, fill: true);
            if (fps >= 0)
                prefs.SetFpsLimit(limits[fps]);

            forms.GroupTitle(words["settings.keys"]);
            foreach (var key in Enum.GetValues<ShellKey>())
            {
                forms.Row(words[$"settings.key{key}"], Label + 40, () =>
                {
                    var label = Binding == key ? words["settings.pressKey"] : KeyLabel(prefs.KeyOf(key));
                    if (forms.Button($"settings:key:{key}", label, active: Binding == key, grow: 1))
                        Binding = Binding == key ? null : key;
                });
            }
        }, anchor, words["common.back"], modal: modal);
        // Escape, отменивший перевешивание клавиши, окно не закрывает.
        if (back && wasBinding && hits.Contains("escape"))
            back = false;
        if (back)
            Binding = null;
        return back;

        void Volume(string id, string label, float value, Action<float> set) =>
            forms.Row(label, Label, () =>
            {
                var next = forms.Slider(id, value, 0, 1, 0.05, Inner - Label - 48 - 16, audio.Muted);
                if (next != value)
                    set((float)next);
                forms.Gui.Box(new Style { Width = 40, Direction = Direction.Row, Justify = Align.End }, () => forms.Gui.Text(ShellPrefs.Percent(value)));
            });
    }

    /// <summary>Клавиша для подписи: «F11», «Enter».</summary>
    public static string KeyLabel(string name) => name.Length switch
    {
        0 => "—",
        1 => name.ToUpperInvariant(),
        _ => char.ToUpperInvariant(name[0]) + name[1..],
    };
}
