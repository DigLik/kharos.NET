using Base.Shared.Orders;
using Kharos.Mod.Sdk.Client;
using MemoryPack;

namespace Base.Client;

/// <summary>
/// Раздел Controls: выбор и приказы (client.controls: select, orders). Порядок за кадр: Alt+щелчок — сигнал союзникам
/// (метка на карте); постановка (Controls/Placing.cs: призрак здания, протяжка покрытия, точка сбора правой кнопкой,
/// границы зон) — пока включён режим из <see cref="Modes"/>, она держит мышь и выделение с приказами молчат; затем выделение
/// (Controls/SelectControl.cs: рамка, двойной щелчок, группы, отметки) и приказы правой кнопкой (Controls/OrderControl.cs).
/// </summary>
internal sealed class ControlsSection : Section
{
    private SelectControl _select = null!;
    private OrderControl _orders = null!;
    private PlacementControl _placing = null!;

    public override void Setup(ClientBuilder client) => client.Controls("base.controls", Frame);

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
        if (Profile is null)
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
        // Пока выбирают место, выделение только рисуется (кольца видны), а мышь и клавиши достаются постановке.
        var busy = _placing.Update(c);
        if (Profile.Controls.Contains("select"))
            _select.Update(c, !busy);
        if (!busy && Profile.Controls.Contains("orders"))
            _orders.Update(c);
    }
}
