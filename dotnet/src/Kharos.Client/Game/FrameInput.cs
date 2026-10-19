using Kharos.Client.Ui;
using Kharos.Mod.Abi;
using Kharos.Protocol;
using Gui = Kharos.Client.Ui.Gui;

namespace Kharos.Client.Game;

/// <summary>
/// Ввод кадра от оболочки (окно, Silk.NET Input или тест): время, экран, указатель, кнопки, колесо, клавиши. То, что
/// <see cref="UiInput"/> берёт интерфейс, плюс зажатые кнопки и клавиши для управления модов.
/// </summary>
public record struct ClientInput()
{
    /// <summary>Секунды с запуска.</summary>
    public double Time { get; set; }
    /// <summary>Экран в логических пикселях и физических на логический.</summary>
    public float Width { get; set; } = 1280;
    public float Height { get; set; } = 720;
    public float Scale { get; set; } = 1;
    /// <summary>Указатель в логических пикселях; null — вне окна.</summary>
    public (float X, float Y)? Pointer { get; set; }
    public PointerButtons Down { get; set; }
    public PointerButtons Pressed { get; set; }
    public PointerButtons Released { get; set; }
    public float Wheel { get; set; }
    public bool Shift { get; set; }
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Touch { get; set; }
    public bool Focused { get; set; } = true;
    /// <summary>Зажатые клавиши ("a", "1", "shift", "left", "escape") и нажатые за кадр.</summary>
    public IReadOnlyList<string> Keys { get; set; } = [];
    public IReadOnlyList<string> Hits { get; set; } = [];
    /// <summary>Набранный за кадр текст.</summary>
    public string Text { get; set; } = "";

    /// <summary>Язык игрока (ABI 2): ru, en, … — моды берут его для своих строк.</summary>
    public string Lang { get; set; } = "ru";

    /// <summary>
    /// Правки полей ввода мода (Field) за кадр: номер поля и новое значение (ABI 2). Пусто — берутся правки интерфейса
    /// кадра (<see cref="Gui.FrameEdits"/>); заполненное оболочкой или тестом идёт как есть.
    /// </summary>
    public IReadOnlyList<(string Id, string Value)> Edits { get; set; } = [];

    /// <summary>Режим оболочки (ABI 2): ClientAbi.Shell* — меню поверх партии, редактор, запись; ставит оболочка (Shell.ShellApp.ShellFlags).</summary>
    public byte Shell { get; set; }

    /// <summary>Что из этого берёт интерфейс.</summary>
    public readonly UiInput ToUi() => new()
    {
        Time = Time, Width = Width, Height = Height, Scale = Scale, Pointer = Pointer, Pressed = Pressed, Released = Released,
        Wheel = Wheel, Shift = Shift, Ctrl = Ctrl, Alt = Alt, Touch = Touch,
        Text = Text, Hits = Hits, Keys = Keys,
    };
}

/// <summary>Время и игрок кадра — то, что клиент знает о партии к кадру.</summary>
public readonly record struct FrameClock(double Time, double Dt, ulong Tick, double Alpha, double TickDt, uint Player)
{
    /// <summary>Секунды игры, сглаженные между тиками.</summary>
    public double Clock => (Tick + Alpha) * TickDt;
}

/// <summary>Запись входа кадра клиентского мода (ClientAbi «Вход кадра»).</summary>
public sealed class FrameInput
{
    private readonly List<string> _ids = [];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <summary>
    /// Вход кадра: время, экран, камера, ввод, состояние интерфейса после <see cref="Gui.Begin"/> (места, наведение,
    /// щелчки по узлам прошлого кадра), звук, сигналы хоста, атмосфера, оболочка (язык, правки полей, режим, ревизия
    /// ростера roster).
    /// </summary>
    public void Write(AbiWriter w, in FrameClock clock, in ClientInput input, CameraState camera, Gui gui,
        AtmosphereState atmosphere, IReadOnlyList<ReplicaSignal> signals, double? audioNow, IReadOnlyList<uint> ended, uint roster = 0)
    {
        w.Clear();
        w.F64(clock.Time).F64(clock.Dt).U64(clock.Tick).F64(clock.Alpha).F64(clock.Clock).F64(clock.TickDt).U32(clock.Player);
        w.F32(input.Width).F32(input.Height).F32(input.Scale).F64(camera.X).F64(camera.Y).F64(camera.Zoom);

        byte flags = 0;
        if (input.Pointer is not null) flags |= ClientAbi.InputInside;
        if (input.Focused) flags |= ClientAbi.InputFocused;
        if (input.Touch) flags |= ClientAbi.InputTouch;
        if (gui.Covered) flags |= ClientAbi.InputOverUi;
        if (input.Shift) flags |= ClientAbi.InputShift;
        if (input.Ctrl) flags |= ClientAbi.InputCtrl;
        if (input.Alt) flags |= ClientAbi.InputAlt;
        var (x, y) = input.Pointer ?? (float.NaN, float.NaN);
        // Поле ввода в фокусе или модальное окно забирают клавиши: игре остаются только Enter, Escape, Tab и стрелки
        // вверх и вниз (их поле отдаёт панелям), а под модальным окном — ничего, и нажатие мира не начинается.
        var capture = gui.CapturesKeys;
        var modal = gui.ModalOpen;
        var keys = capture ? [] : input.Keys;
        var hits = !capture ? input.Hits : modal ? [] : [.. input.Hits.Where(key => Array.IndexOf(Gui.FieldPassKeys, key) >= 0)];
        var text = capture ? "" : input.Text;
        var pressed = modal ? PointerButtons.None : input.Pressed;
        var wheel = modal ? 0 : input.Wheel;
        w.U8(flags).F32(x).F32(y).U8(Buttons(input.Down)).U8(Buttons(pressed)).U8(Buttons(input.Released)).F32(wheel);
        w.U16((ushort)Math.Min(keys.Count, ushort.MaxValue));
        for (var i = 0; i < Math.Min(keys.Count, ushort.MaxValue); i++) w.Str(keys[i]);
        w.U16((ushort)Math.Min(hits.Count, ushort.MaxValue));
        for (var i = 0; i < Math.Min(hits.Count, ushort.MaxValue); i++) w.Str(hits[i]);
        w.Str(text);

        // Интерфейс: номера узлов прошлого кадра и щелчков этого.
        byte ui = 0;
        if (gui.Covered) ui |= ClientAbi.UiCovered;
        if (gui.HoveredButton) ui |= ClientAbi.UiHoveredButton;
        if (gui.PressedButton is { } disabled) ui |= (byte)(ClientAbi.UiPressedButton | (disabled ? ClientAbi.UiPressedDisabled : 0));
        w.F32(gui.Width).F32(gui.Height).F64(gui.Now).U8(ui);
        _ids.Clear();
        _seen.Clear();
        foreach (var hit in gui.Hits)
            if (_seen.Add(hit.Id)) _ids.Add(hit.Id);
        foreach (var id in gui.FrameClicks.Keys)
            if (_seen.Add(id)) _ids.Add(id);
        w.U16((ushort)Math.Min(_ids.Count, ushort.MaxValue));
        foreach (var id in _ids.Take(ushort.MaxValue))
        {
            byte state = 0;
            if (gui.Hovered(id)) state |= ClientAbi.IdHovered;
            if (gui.Within(id)) state |= ClientAbi.IdWithin;
            if (gui.Pressing(id)) state |= ClientAbi.IdPressing;
            if (gui.TipDue(id)) state |= ClientAbi.IdTipDue;
            var click = gui.Clicked(id);
            if (click is not null) state |= ClientAbi.IdClicked;
            var held = gui.Held(id);
            if (held is not null) state |= ClientAbi.IdHeld;
            var rect = gui.RectOf(id) ?? default;
            w.Str(id).U8(state).F32(rect.X).F32(rect.Y).F32(rect.W).F32(rect.H);
            if (held is { } at) w.F32(at.U).F32(at.V);
            if (click is { } c)
                w.U8(c.Button).U8((byte)((c.Shift ? ClientAbi.ClickShift : 0) | (c.Ctrl ? ClientAbi.ClickCtrl : 0) | (c.Alt ? ClientAbi.ClickAlt : 0) | (c.Long ? ClientAbi.ClickLong : 0)));
        }

        w.U8(audioNow is null ? (byte)0 : (byte)1).F64(audioNow ?? 0);
        w.U16((ushort)Math.Min(ended.Count, ushort.MaxValue));
        for (var i = 0; i < Math.Min(ended.Count, ushort.MaxValue); i++) w.U32(ended[i]);

        w.U16((ushort)Math.Min(signals.Count, ushort.MaxValue));
        for (var i = 0; i < Math.Min(signals.Count, ushort.MaxValue); i++)
            w.U64(signals[i].Tick).Str(signals[i].Signal.Name).Blob(signals[i].Signal.Payload);

        w.F32(atmosphere.Light).F32(atmosphere.WindX).F32(atmosphere.WindY).F32(atmosphere.Precipitation);

        w.Str(input.Lang);
        IReadOnlyList<(string Id, string Value)> list = input.Edits.Count > 0 ? input.Edits : gui.FrameEdits;
        var edits = Math.Min(list.Count, ushort.MaxValue);
        w.U16((ushort)edits);
        for (var i = 0; i < edits; i++)
            w.Str(list[i].Id).Str(list[i].Value);
        w.U8(input.Shell).U32(roster);
    }

    private static byte Buttons(PointerButtons buttons) =>
        (byte)(((buttons & PointerButtons.Left) != 0 ? ClientAbi.ButtonLeft : 0) | ((buttons & PointerButtons.Right) != 0 ? ClientAbi.ButtonRight : 0) | ((buttons & PointerButtons.Middle) != 0 ? ClientAbi.ButtonMiddle : 0));
}
