using Base.Shared;
using Base.Shared.Orders;
using Base.Shared.Signals;
using Kharos.Engine.World;
using MemoryPack;

namespace Kharos.Base.Scenario;

// Правки мира для подготовки сцены — приказы edit.* с правами редактора и администратора (Base.Shared/Orders/EditOrders.cs,
// editOps.ts в TS). Каждая применяется сразу (Flush: без тика), так что вслед за ней мир готов, а Put* возвращают
// номер новой сущности. Обработчики edit.* пишет ядро base (Б1); нет обработчика — помощники бросают NotImplementedException
// с понятной причиной. Что нужно только данным и не требует мода — пишите в мир прямо (Set/Add из Sim.World.cs).
public sealed partial class Sim
{
    /// <summary>Права, с которыми идут правки: редактор карты и администратор.</summary>
    public const OrderRights EditRights = OrderRights.Editor | OrderRights.Admin;

    /// <summary>Любая правка: приказ от имени игрока 0 («ничей») с правами редактора, сразу применённый.</summary>
    public void Edit<T>(T payload) where T : class
    {
        var name = NameOf(typeof(T));
        RequireHandler(name);
        Send(0, name, MemoryPackSerializer.Serialize(payload), EditRights);
        Flush();
    }

    /// <summary>
    /// Правка, создающая сущность: номер первой созданной с компонентом kind (правка может сперва завести счёт игрока, у
    /// которого его не было); отказ мода (refused) или молчание — исключение.
    /// </summary>
    private uint Spawned<T>(T payload, string kind) where T : class
    {
        var name = NameOf(typeof(T));
        var first = World.NextEntity;
        var before = Signals.Count;
        Edit(payload);
        for (var entity = first; entity < World.NextEntity; entity++)
            if (World.Alive(entity) && Has(entity, kind))
                return entity;
        var reason = Signals.Skip(before).Where(signal => signal.Name == RefusedSignal.Name)
            .Select(signal => MemoryPackSerializer.Deserialize<RefusedSignal>(signal.Payload)?.Reason).FirstOrDefault();
        throw new InvalidOperationException($"{name}: сущность не создана" + (reason is null ? "" : $" ({reason})"));
    }

    private void RequireHandler(string name)
    {
        if (!Host.HasOrder(name))
            throw new NotImplementedException($"{name}: в моде base нет обработчика правки (ждёт ядра Б1)");
    }

    // ===== Создание =====

    /// <summary>Готовый юнит игрока в точке (x, y): центр тайла — x + 0,5, как у <c>spawnUnit</c> в TS. Возвращает его номер.</summary>
    public uint PutUnit(string type, uint player, double x, double y) =>
        Spawned(new EditUnitOrder { Type = type, X = x, Y = y, Player = player }, Names.Unit.Name);

    /// <summary>Готовое здание игрока, левый верхний тайл (x, y) (<c>placeBuilding</c>). Возвращает его номер.</summary>
    public uint PutBuilding(string type, int x, int y, uint player) =>
        Spawned(new EditBuildingOrder { Type = type, X = x, Y = y, Player = player }, Names.Building.Name);

    /// <summary>Месторождение с запасом reserve, левый верхний тайл (x, y).</summary>
    public void PutDeposit(string kind, int x, int y, double reserve) =>
        Edit(new EditDepositOrder { Kind = kind, X = x, Y = y, Reserve = reserve });

    /// <summary>Сменить вид и/или остаток месторождений на местах spots (x и y подряд); не задано — прежнее.</summary>
    public void SetDeposit(int[] spots, string? kind = null, double? left = null) =>
        Edit(new EditSetDepositOrder { Spots = spots, Kind = kind, Left = left });

    /// <summary>Убрать месторождения на местах spots (x и y подряд).</summary>
    public void RemoveDeposit(params int[] spots) => Edit(new EditRemoveDepositOrder { Spots = spots });

    /// <summary>Покрасить землю кистью размера size с серединой (x, y).</summary>
    public void Paint(int x, int y, int size, Brush brush) => Edit(new EditPaintOrder { X = x, Y = y, Size = size, Brush = brush });

    /// <summary>Новый игрок без юнитов (edit.addPlayer); номер ему выдаёт мод.</summary>
    public void AddPlayer() => Edit(new EditAddPlayerOrder());

    // ===== Изменение =====

    /// <summary>Кредиты игрока ровно столько.</summary>
    public void SetCredits(uint player, double amount) => Edit(new EditCreditsOrder { Player = player, Amount = amount });

    /// <summary>Сколько груза good лежит в складе сущности (<c>setStock</c>); good — имя товара из data (metal, siliconOre…).</summary>
    public void SetStock(uint entity, string good, double amount) => Edit(new EditStockOrder { Entity = entity, Good = good, Amount = amount });

    /// <summary>Прочность долей от полной (0…1).</summary>
    public void SetHealth(double share, params uint[] entities) => Edit(new EditHealthOrder { Entities = entities, Share = share });

    /// <summary>Отдать сущности игроку.</summary>
    public void SetOwner(uint player, params uint[] entities) => Edit(new EditOwnerOrder { Entities = entities, Player = player });

    /// <summary>Убрать сущности из мира.</summary>
    public void Erase(params uint[] entities) => Edit(new EditEraseOrder { Entities = entities });

    /// <summary>Сдвинуть сущности на (dx, dy) тайлов.</summary>
    public void Move(int dx, int dy, params uint[] entities) => Edit(new EditMoveOrder { Entities = entities, Dx = dx, Dy = dy });

    /// <summary>Повернуть сущности.</summary>
    public void SetFacing(double angle, params uint[] entities) => Edit(new EditFacingOrder { Entities = entities, Angle = angle });

    /// <summary>Повернуть турели сущностей; index — какую (нет — все).</summary>
    public void SetTurret(double angle, int? index, params uint[] entities) => Edit(new EditTurretOrder { Entities = entities, Angle = angle, Index = index });

    /// <summary>Снять с сущностей дела и очереди.</summary>
    public void ClearTasks(params uint[] entities) => Edit(new EditClearTasksOrder { Entities = entities });

    /// <summary>Выполнить приказ за игрока сразу, без ожидания тика (<c>orderNow</c>).</summary>
    public void OrderNow<T>(uint player, T order) where T : class =>
        Edit(new EditOrderOrder { Player = player, Order = NameOf(typeof(T)), Payload = MemoryPackSerializer.Serialize(order) });
}
