using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Раздел Controls: выбор и приказы (client.controls: select, orders). TODO(К2): рамка и группы, Controls/Placing.cs
/// (постановка, граница зоны, кисть покрытия, точка сбора).
/// </summary>
internal sealed class ControlsSection : Section
{
    private SelectControl _select = null!;
    private OrderControl _orders = null!;

    public override void Setup(ClientBuilder client) => client.Controls("base.controls", Frame);

    public override void Load(BaseState state)
    {
        base.Load(state);
        _select = new SelectControl(state);
        _orders = new OrderControl(state);
    }

    public override void Frame(ClientContext c)
    {
        if (Profile is null)
            return;
        if (Profile.Controls.Contains("select"))
            _select.Update(c);
        if (Profile.Controls.Contains("orders"))
            _orders.Update(c);
    }
}
