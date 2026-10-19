using Base.Shared.Orders;
using Kharos.Mod.Sdk.Client;
using MemoryPack;

namespace Base.Client;

/// <summary>
/// Раздел Controls: выбор и приказы (client.controls: select, orders). Порядок за кадр: Alt+щелчок — сигнал союзникам
/// (метка на карте); постановка (Controls/Placing.cs: призрак здания, протяжка покрытия, точка сбора правой кнопкой,
/// границы зон) — пока включён режим из <see cref="Modes"/>, она держит мышь и выделение с приказами молчат; затем выделение
/// (Controls/SelectControl.cs: рамка, двойной щелчок, группы, отметки) и приказы правой кнопкой (Controls/OrderControl.cs).
/// Esc, когда отменять нечего (ни протяжки, ни режима, ни выделения, ни открытого окна), просит оболочку открыть меню
/// игры (controls.ts: onMenu); F10 оболочки остаётся запасной клавишей.
/// </summary>
internal sealed class ControlsSection : Section
{
    private SelectControl _select = null!;
    private OrderControl _orders = null!;
    private PlacementControl _placing = null!;

    /// <summary>Было ли в начале кадра открыто окно, которое Esc закрывает само (исследования): окна рисуются раньше управления.</summary>
    private bool _window;

    public override void Setup(ClientBuilder client)
    {
        client.Camera("base.controls.windows", c => _window = c.Shared.TryGetValue(ResearchSection.OpenKey, out var open) && open is true);
        client.Controls("base.controls", Frame);
    }

    public override void Load(BaseState state)
    {
        base.Load(state);
        var world = new PlaceWorld(state);
        _select = new SelectControl(state, world);
        _orders = new OrderControl(state);
        _placing = new PlacementControl(state, world);
    }

    public override void Frame(ClientContext c)
    {
        // В редакторе мышь и клавиши — у его панели (Editor/EditorMouse.cs).
        if (Profile is null || c.Shared.TryGetValue(EditorModel.OpenKey, out var editor) && editor is true)
            return;
        var input = c.Input;
        // Alt+щелчок — метка союзникам, а не выделение и не постановка.
        if (input.Alt && input.Pressed(PointerButton.Left) && input.Inside && !input.OverUi && (c.Shell & ShellMode.Menu) == 0)
        {
            c.Send(PingOrder.Name, MemoryPackSerializer.Serialize(new PingOrder { X = input.WorldX, Y = input.WorldY }));
            if (Profile.Controls.Contains("select"))
                _select.Update(c, false);
            return;
        }
        // Esc нечего отменять — меню игры (до выделения: оно Esc снимает).
        var menu = input.Hits.Contains("escape") && !_window && (c.Shell & (ShellMode.Menu | ShellMode.Editor)) == 0 && !Modes.Active(c)
            && Shared.Selection(c).Length == 0 && !input.Down(PointerButton.Left);
        // Пока выбирают место, выделение только рисуется (кольца видны), а мышь и клавиши достаются постановке.
        var busy = _placing.Update(c);
        if (menu)
            c.Ask(ShellAction.Menu);
        if (Profile.Controls.Contains("select"))
            _select.Update(c, !busy);
        if (!busy && Profile.Controls.Contains("orders"))
            _orders.Update(c);
    }
}
