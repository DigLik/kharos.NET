using Base.Shared.Orders;
using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;
using MemoryPack;

namespace Base.Client;

/// <summary>
/// Приказы правой кнопкой — перенос controls/orders.luau: выделенные юниты едут в точку, выделенный завод ставит туда
/// точку сбора. Касанием — долгое нажатие на земле. Куда едут — видно пунктиром пути у выделенных (SelectControl).
/// </summary>
internal sealed class OrderControl(BaseState state)
{
    /// <summary>Долгое касание, секунд.</summary>
    private const double Hold = 0.5;

    private (double Time, float X, float Y)? _held;

    private Parts P => state.P;

    private void Order(ClientContext c, double x, double y)
    {
        var units = new List<uint>();
        uint building = 0;
        foreach (var entity in Shared.Selection(c))
        {
            if (c.Has(entity, P.Drive))
                units.Add(entity);
            else if (c.Has(entity, P.Producer))
                building = entity;
        }
        if (units.Count > 0)
        {
            c.Send("move", MemoryPackSerializer.Serialize(new MoveOrder { Units = [.. units], X = (int)Math.Floor(x), Y = (int)Math.Floor(y) }));
            c.Sound("order", 0.35f);
        }
        else if (building != 0)
        {
            c.Send("rally", MemoryPackSerializer.Serialize(new RallyOrder { Building = building, X = (int)Math.Floor(x), Y = (int)Math.Floor(y) }));
            c.Sound("place", 0.35f);
        }
    }

    public void Update(ClientContext c)
    {
        var input = c.Input;
        if (input.Pressed(PointerButton.Right) && input.Inside && !input.OverUi)
            Order(c, input.WorldX, input.WorldY);
        // Касание: долгое нажатие на месте — приказ.
        if (input.Pressed(PointerButton.Left) && input.Touch && !input.OverUi)
            _held = (c.Time, input.X, input.Y);
        if (_held is { } held)
        {
            var moved = Math.Abs(input.X - held.X) + Math.Abs(input.Y - held.Y) > 8;
            if (!input.Down(PointerButton.Left) || moved)
                _held = null;
            else if (c.Time - held.Time > Hold)
            {
                Order(c, input.WorldX, input.WorldY);
                c.Shared[Shared.SwallowKey] = true;
                _held = null;
            }
        }
        if (input.Hits.Contains("x"))
            c.Send("stop", MemoryPackSerializer.Serialize(new StopOrder { Units = Shared.Selection(c) }));
    }
}
