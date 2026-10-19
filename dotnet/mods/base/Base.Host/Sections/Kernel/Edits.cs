using Base.Host.Kernel;
using Base.Shared.Orders;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Kernel;

/// <summary>
/// Правки редактора edit.* (editOps.ts, editor.ts): режим бога — без денег, технологий и строителей, но правила места
/// те же, что в игре. Права редактора проверяет диспетчер. Что не удалось — отказ refused с ключом (building, unit,
/// deposit, move, order). Месторождение — предмет слоя deposits левым верхним тайлом: поля kind (номер записи по
/// порядку deposits.json) и reserve; сущность Deposit (сколько добыто) стоит на том же тайле.
/// </summary>
internal static class Edits
{
    /// <summary>Сторона месторождения (DEPOSIT_SIZE).</summary>
    private const int DepositSize = 2;

    /// <summary>Какую долю тайлов круга красит разброс (SPRAY_SHARE).</summary>
    private const double SprayShare = 0.3;

    /// <summary>Самый большой радиус горы (MAX_PEAK_RADIUS).</summary>
    private const double MaxPeakRadius = 2.75;

    /// <summary>Здания сменили хозяина (правка edit.owner): кэш сетей забывает посчитанное (ставит раздел игроков).</summary>
    public static Action? OwnersChanged { get; set; }

    public static void Setup()
    {
        Commands.Handle<EditPaintOrder>(EditPaintOrder.Name, Paint);
        Commands.Handle<EditBuildingOrder>(EditBuildingOrder.Name, PutBuilding);
        Commands.Handle<EditUnitOrder>(EditUnitOrder.Name, PutUnit);
        Commands.Handle<EditDepositOrder>(EditDepositOrder.Name, (call, player, edit) =>
            Done(call, player, edit, PutDeposit(call, edit.X, edit.Y, edit.Kind, edit.Reserve), "deposit"));
        Commands.Handle<EditSetDepositOrder>(EditSetDepositOrder.Name, SetDeposit);
        Commands.Handle<EditRemoveDepositOrder>(EditRemoveDepositOrder.Name, (call, _, edit) =>
        {
            foreach (var (x, y) in Spots(edit.Spots))
                if (DepositAt(x, y) is not null)
                    DropDeposit(call, x, y);
            return true;
        });
        Commands.Handle<EditEraseOrder>(EditEraseOrder.Name, (call, _, edit) =>
        {
            foreach (var entity in Alive(call, edit.Entities))
                Erase(call, entity);
            return true;
        });
        Commands.Handle<EditMoveOrder>(EditMoveOrder.Name, (call, player, edit) => Done(call, player, edit, MoveGroup(call, Alive(call, edit.Entities), edit.Dx, edit.Dy, Spots(edit.Deposits)), "move"));
        Commands.Handle<EditOwnerOrder>(EditOwnerOrder.Name, (call, _, edit) =>
        {
            Accounts.Ensure(call, edit.Player);
            var buildings = false;
            foreach (var entity in Alive(call, edit.Entities))
            {
                buildings |= call.Has(entity, S.Building);
                foreach (var target in (uint[])[entity, .. Factory.TurretsOf(call, entity)])
                    if (call.Has(target, S.Owner))
                        call.Set(target, S.Owner).Number(S.OwnerPlayer, edit.Player);
            }
            // Сети зданий — чьи они (resetZones в setOwner): смену хозяина ревизия мира не замечает.
            if (buildings)
                OwnersChanged?.Invoke();
            return true;
        });
        Commands.Handle<EditHealthOrder>(EditHealthOrder.Name, (call, _, edit) =>
        {
            foreach (var entity in Alive(call, edit.Entities))
                if (call.Has(entity, S.Health))
                    call.Set(entity, S.Health).Number(S.HealthValue, call.Num(entity, S.Health, S.HealthMax) * Math.Min(1, Math.Max(0.01, edit.Share)));
            return true;
        });
        Commands.Handle<EditFacingOrder>(EditFacingOrder.Name, (call, _, edit) =>
        {
            foreach (var entity in Alive(call, edit.Entities))
                if (call.Has(entity, S.Unit) && call.Has(entity, S.Facing))
                    call.Set(entity, S.Facing).Number(S.FacingAngle, Wrap(edit.Angle));
            return true;
        });
        Commands.Handle<EditTurretOrder>(EditTurretOrder.Name, (call, _, edit) =>
        {
            foreach (var entity in Alive(call, edit.Entities))
            {
                var hull = call.Has(entity, S.Unit) ? call.Num(entity, S.Facing, S.FacingAngle) : 0;
                var turrets = Factory.TurretsOf(call, entity);
                for (var i = 0; i < turrets.Count; i++)
                    if (edit.Index is null || edit.Index == i)
                        call.Set(turrets[i], S.Mount).Number(S.MountAngle, Wrap(edit.Angle - hull));
            }
            return true;
        });
        Commands.Handle<EditStockOrder>(EditStockOrder.Name, (call, _, edit) =>
        {
            if (call.Alive(edit.Entity) && call.Has(edit.Entity, S.Stock) && S.Stock.Find(edit.Good) is { } field)
                call.Set(edit.Entity, S.Stock).Number(field, Math.Max(0, Math.Floor(edit.Amount + 0.5)));
            return true;
        });
        Commands.Handle<EditClearTasksOrder>(EditClearTasksOrder.Name, (call, _, edit) =>
        {
            var units = Alive(call, edit.Entities);
            Commands.ClearOrders(call, units);
            foreach (var entity in units)
            {
                call.Remove(entity, S.Path);
                Commands.Free(call, entity);
            }
            return true;
        });
        Commands.Handle<EditOrderOrder>(EditOrderOrder.Name, (call, player, edit) =>
            Done(call, player, edit, Commands.Known(edit.Order) && Commands.AcceptBytes(call, edit.Player, edit.Order, edit.Payload), "order"));
        Commands.Handle<EditCreditsOrder>(EditCreditsOrder.Name, (call, _, edit) =>
        {
            Accounts.SetCredits(call, edit.Player, edit.Amount);
            return true;
        });
        Commands.Handle<EditCameraOrder>(EditCameraOrder.Name, (call, _, edit) =>
        {
            var account = Accounts.Ensure(call, edit.Player);
            if (account == 0)
                return false;
            var bytes = edit.Clear ? [] : new byte[24];
            if (!edit.Clear)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(0), edit.X);
                BitConverter.TryWriteBytes(bytes.AsSpan(8), edit.Y);
                BitConverter.TryWriteBytes(bytes.AsSpan(16), edit.Zoom);
            }
            call.Set(account, S.Player).Bytes(S.PlayerCamera, bytes);
            return true;
        });
        Commands.Handle<EditAddPlayerOrder>(EditAddPlayerOrder.Name, (call, _, _) =>
        {
            var players = Accounts.Players(call);
            Accounts.Ensure(call, players.Count == 0 ? 1 : players.Max() + 1);
            return true;
        });
        Commands.Handle<EditStartOrder>(EditStartOrder.Name, (call, _, edit) => edit.Player != 0 && Spawning.StartKit(call, edit.Player, edit.X, edit.Y).Count > 0);
    }

    /// <summary>Итог правки: не вышла — отказ с ключом.</summary>
    private static bool Done<T>(Call call, uint player, T edit, bool done, string reason) where T : class
    {
        if (!done)
            call.Refuse(player, Commands.NameOf(edit), reason);
        return done;
    }

    private static List<uint> Alive(Call call, uint[] entities) => [.. entities.Distinct().Where(call.Alive)];

    private static IEnumerable<(int X, int Y)> Spots(int[] spots)
    {
        for (var i = 0; i + 1 < spots.Length; i += 2)
            yield return (spots[i], spots[i + 1]);
    }

    /// <summary>Угол к промежутку от −π до π (wrap).</summary>
    public static double Wrap(double angle) => angle - Math.PI * 2 * Math.Floor(angle / (Math.PI * 2) + 0.5);

    // ===== Здания и юниты =====

    /// <summary>Готовое здание игроку (0 — ничьё), если оно там встанет (putBuilding).</summary>
    private static bool PutBuilding(Call call, uint player, EditBuildingOrder edit)
    {
        if (!Placement.CanPlace(call, edit.Type, edit.X, edit.Y))
            return Done(call, player, edit, false, "building");
        if (edit.Player != 0)
            Accounts.Ensure(call, edit.Player);
        Factory.PlaceBuilding(call, edit.Type, edit.X, edit.Y, edit.Player);
        return true;
    }

    /// <summary>Может ли юнит вида type стоять на тайле: летающему — на карте, наземному — проходимо (пехоте и у обрыва).</summary>
    private static bool CanPut(string type, int x, int y)
    {
        if (Factory.Unit(type) is not { } spec)
            return false;
        if (spec.Kind == "air")
            return Placement.InBounds(x, y);
        return Tools.Walkable(x, y, spec.Kind == "infantry" ? PathFlags.Foot : PathFlags.None);
    }

    /// <summary>Юнит игроку в точку (x, y), если ему там стоять (putUnit).</summary>
    private static bool PutUnit(Call call, uint player, EditUnitOrder edit)
    {
        if (!double.IsFinite(edit.X) || !double.IsFinite(edit.Y) || !CanPut(edit.Type, (int)Math.Floor(edit.X), (int)Math.Floor(edit.Y)))
            return Done(call, player, edit, false, "unit");
        if (edit.Player != 0)
            Accounts.Ensure(call, edit.Player);
        Factory.SpawnUnit(call, edit.Type, edit.Player, edit.X, edit.Y);
        return true;
    }

    /// <summary>Убирает здание или юнит с турелями, без взрыва и груза на земле; турель саму по себе — нет (erase).</summary>
    private static void Erase(Call call, uint entity)
    {
        if (call.Num(entity, S.Mount, S.MountParent) != 0)
            return;
        Commands.Free(call, entity);
        call.Destroy(entity);
    }

    /// <summary>
    /// Сдвигает юниты, здания и месторождения вместе на (dx, dy): всё или ничего (moveGroup). Сдвигаемые друг другу не
    /// мешают.
    /// </summary>
    private static bool MoveGroup(Call call, List<uint> entities, int dx, int dy, IEnumerable<(int X, int Y)> deposits)
    {
        var spots = deposits.Where(spot => DepositAt(spot.X, spot.Y) is not null).ToList();
        if (dx == 0 && dy == 0)
            return true;
        if (!spots.All(spot => CanPutDeposit(spot.X + dx, spot.Y + dy, spots)))
            return false;
        var buildings = entities.Where(entity => call.Has(entity, S.Building) && call.Has(entity, S.Position)).ToList();
        var units = entities.Where(entity => !call.Has(entity, S.Building) && call.Has(entity, S.Unit) && call.Has(entity, S.Position)).ToList();
        var moving = new HashSet<uint>(buildings);
        foreach (var building in buildings)
        {
            var (x, y) = call.At(building);
            if (call.BuildingType(building) is not { } type || !Placement.CanPlace(call, type, (int)x + dx, (int)y + dy, 0, moving))
                return false;
        }
        foreach (var unit in units)
        {
            var (x, y) = call.At(unit);
            if (call.UnitType(unit) is not { } type || !CanPut(type, (int)Math.Floor(x + dx), (int)Math.Floor(y + dy)))
                return false;
        }
        foreach (var entity in buildings.Concat(units))
        {
            var (x, y) = call.At(entity);
            call.Set(entity, S.Position).Number(S.PositionX, x + dx).Number(S.PositionY, y + dy);
            if (call.Has(entity, S.Unit))
                call.Remove(entity, S.Path);
        }
        // Месторождения сначала все уходят, потом все встают.
        var moved = spots.Select(spot => (spot.X, spot.Y, Kind: DepositAt(spot.X, spot.Y)!.Fields[0], Left: ReserveLeft(call, spot.X, spot.Y))).ToList();
        foreach (var spot in moved)
            DropDeposit(call, spot.X, spot.Y);
        foreach (var spot in moved)
            AddDeposit(call, spot.X + dx, spot.Y + dy, spot.Kind, spot.Left);
        return true;
    }

    // ===== Месторождения =====

    /// <summary>Месторождение левым верхним тайлом (x, y); нет — null.</summary>
    private static MapItem? DepositAt(int x, int y)
    {
        if (S.Deposits is not { } layer)
            return null;
        foreach (var item in Tools.Items(layer, 0, x, y, 0.5))
            if (item.X == x && item.Y == y)
                return item;
        return null;
    }

    /// <summary>Сущность Deposit на месте месторождения (сколько добыто); нет — 0.</summary>
    private static uint DepositEntity(Call call, int x, int y)
    {
        foreach (var entity in Tools.Near(x, y, 0.5, S.Deposit))
            if (call.At(entity) == (x, y))
                return entity;
        return 0;
    }

    /// <summary>Сколько в месторождении осталось: запас без добытого (reserveLeft).</summary>
    private static double ReserveLeft(Call call, int x, int y) =>
        Math.Max(0, (DepositAt(x, y)?.Fields[1] ?? 0) - call.Num(DepositEntity(call, x, y), S.Deposit, S.DepositMined));

    /// <summary>Номер вида месторождения: место записи в deposits.json; нет — −1.</summary>
    private static int KindOf(string kind) => Core.Tables.Deposits.Keys.ToList().IndexOf(kind);

    /// <summary>
    /// Можно ли положить месторождение левым верхним тайлом в (x, y): где встала бы шахта — скала не у подножия обрыва, —
    /// не внахлёст с другим; ignore — переносимые (canPutDeposit).
    /// </summary>
    private static bool CanPutDeposit(int x, int y, IReadOnlyCollection<(int X, int Y)>? ignore = null)
    {
        if (S.Deposits is not { } layer || !Placement.InBounds(x, y, DepositSize, DepositSize))
            return false;
        foreach (var other in Tools.Items(layer, 0, x, y, DepositSize * 2))
        {
            if (ignore?.Contains(((int)other.X, (int)other.Y)) == true)
                continue;
            if (Math.Abs(other.X - x) < DepositSize && Math.Abs(other.Y - y) < DepositSize)
                return false;
        }
        Span<TileFlags> tiles = stackalloc TileFlags[DepositSize * DepositSize];
        Tools.Tiles(x, y, DepositSize, DepositSize, tiles);
        foreach (var bits in tiles)
            if ((bits & TileFlags.Buildable) == 0)
                return false;
        return true;
    }

    private static void AddDeposit(Call call, int x, int y, double kind, double reserve)
    {
        if (S.Deposits is { } layer)
            call.Commands.PushItem(layer, 0, x, y, [kind, Math.Max(0, Math.Floor(reserve + 0.5))]);
    }

    /// <summary>Убирает месторождение и забывает, сколько из него добыли (dropDeposit).</summary>
    private static void DropDeposit(Call call, int x, int y)
    {
        if (S.Deposits is not { } layer)
            return;
        call.Commands.DeleteItems(layer, 0, x, y);
        if (DepositEntity(call, x, y) is var mined and not 0)
            call.Destroy(mined);
    }

    private static bool PutDeposit(Call call, int x, int y, string kind, double reserve)
    {
        if (KindOf(kind) is var id && id < 0 || !CanPutDeposit(x, y))
            return false;
        AddDeposit(call, x, y, id, reserve);
        return true;
    }

    /// <summary>Сменить вид и/или остаток месторождений; добытое забывается (setDeposit).</summary>
    private static bool SetDeposit(Call call, uint player, EditSetDepositOrder edit)
    {
        var kind = edit.Kind is null ? -1 : KindOf(edit.Kind);
        if (edit.Kind is not null && kind < 0)
            return Done(call, player, edit, false, "deposit");
        foreach (var (x, y) in Spots(edit.Spots))
        {
            if (DepositAt(x, y) is not { } item)
                continue;
            var left = edit.Left ?? ReserveLeft(call, x, y);
            DropDeposit(call, x, y);
            AddDeposit(call, x, y, kind >= 0 ? kind : item.Fields[0], left);
        }
        return true;
    }

    // ===== Земля =====

    /// <summary>
    /// Красит кистью тайлы вокруг (x, y) (paint): что не задано, остаётся прежним; гора встаёт одной вершиной в середине
    /// кисти, а гору, которую кисть задела, стирает целиком.
    /// </summary>
    private static bool Paint(Call call, uint player, EditPaintOrder edit)
    {
        var (x, y, size, brush) = (edit.X, edit.Y, Math.Max(1, edit.Size), edit.Brush);
        if (brush.Terrain == 3)
        {
            var radius = Math.Max(0.7, Math.Min(MaxPeakRadius, size / 2.0));
            var (px, py) = (x + 0.5, y + 0.5);
            var span = (int)Math.Ceiling(radius + 1);
            for (var tileY = y - span; tileY <= y + span; tileY++)
                for (var tileX = x - span; tileX <= x + span; tileX++)
                {
                    var (cx, cy) = (tileX + 0.5, tileY + 0.5);
                    if (!Placement.InBounds(tileX, tileY) || Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy)) >= radius + Math.Sqrt(0.5))
                        continue;
                    // На стыке с соседней горой тайл достаётся ближней вершине.
                    if (Tools.Peak(tileX, tileY, out var ox, out var oy, out _) && Math.Sqrt((ox - cx) * (ox - cx) + (oy - cy) * (oy - cy)) < Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy)))
                        continue;
                    var (_, biome, _, _) = Bytes(tileX, tileY);
                    call.Commands.LandPeak(tileX, tileY, (byte)(brush.Biome ?? biome), px, py, radius);
                }
            return true;
        }
        var tiles = BrushTiles(call, x, y, size, brush.Shape ?? "square");
        var erased = new HashSet<(double, double)>();
        foreach (var (tileX, tileY) in tiles)
            if (Placement.InBounds(tileX, tileY) && Tools.Peak(tileX, tileY, out var ox, out var oy, out var radius) && erased.Add((ox, oy)))
                ErasePeak(call, ox, oy, radius);
        foreach (var (tileX, tileY) in tiles)
        {
            if (!Placement.InBounds(tileX, tileY))
                continue;
            var (terrain, biome, tier, cliff, dunes) = Tile(tileX, tileY);
            if (terrain == 3)
                terrain = 2;
            call.Commands.LandTile(tileX, tileY, new LandEdit((byte)(brush.Terrain ?? terrain), (byte)(brush.Biome ?? biome), (byte)(brush.Tier ?? tier), brush.Cliff ?? cliff, (byte)(brush.Dunes ?? dunes)));
        }
        return true;
    }

    /// <summary>Тайлы кисти размера size с серединой в (x, y): квадрат, круг или разброс (brushTiles).</summary>
    private static List<(int X, int Y)> BrushTiles(Call call, int x, int y, int size, string shape)
    {
        var tiles = new List<(int, int)>();
        var from = size / 2;
        var radius = size / 2.0;
        for (var tileY = y - from; tileY < y - from + size; tileY++)
            for (var tileX = x - from; tileX < x - from + size; tileX++)
            {
                var inside = shape == "square" || Math.Sqrt((tileX - x) * (tileX - x) + (tileY - y) * (tileY - y)) <= radius - 0.25 || size == 1;
                if (inside && (shape != "spray" || size == 1 || call.Random() < SprayShare))
                    tiles.Add((tileX, tileY));
            }
        return tiles;
    }

    /// <summary>Стирает гору целиком: все тайлы её вершины становятся скалой первого яруса.</summary>
    private static void ErasePeak(Call call, double px, double py, double radius)
    {
        var span = (int)Math.Ceiling(radius + 1);
        for (var tileY = (int)Math.Floor(py) - span; tileY <= (int)Math.Floor(py) + span; tileY++)
            for (var tileX = (int)Math.Floor(px) - span; tileX <= (int)Math.Floor(px) + span; tileX++)
            {
                if (!Placement.InBounds(tileX, tileY) || !Tools.Peak(tileX, tileY, out var ox, out var oy, out _) || ox != px || oy != py)
                    continue;
                var (_, biome, _, _) = Bytes(tileX, tileY);
                call.Commands.LandTile(tileX, tileY, new LandEdit(2, biome, 1));
            }
    }

    /// <summary>Байты тайла из слоя land: вид, биом, (третий), рельеф.</summary>
    private static (byte Terrain, byte Biome, byte Third, byte Relief) Bytes(int x, int y)
    {
        if (S.Land is not { } land)
            return (2, 0, 0, 0);
        Span<double> values = stackalloc double[1];
        var bytes = new byte[4];
        for (var channel = 0; channel < 4; channel++)
        {
            Tools.Layer(land, 0, channel, x, y, 1, 1, values);
            bytes[channel] = (byte)values[0];
        }
        // Биом хранится в двух старших битах байта (младшие — примесь соседнего).
        var biome = (byte)(bytes[1] >> 6);
        return (bytes[0], biome, bytes[2], bytes[3]);
    }

    /// <summary>Тайл по полям: вид, биом, ярус, обрыв, барханы.</summary>
    private static (int Terrain, int Biome, int Tier, bool Cliff, int Dunes) Tile(int x, int y)
    {
        var (terrain, biome, _, relief) = Bytes(x, y);
        return (terrain, biome, relief & 3, (relief & 4) != 0, (relief >> 5) & 3);
    }
}
