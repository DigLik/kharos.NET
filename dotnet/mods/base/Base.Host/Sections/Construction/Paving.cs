using Base.Host.Kernel;
using Base.Shared.Rules;
using Kharos.Mod.Sdk;
using static Base.Host.Sections.Construction.Refs;

namespace Base.Host.Sections.Construction;

/// <summary>
/// Покрытие (paving.ts, paved.ts): фундамент и дорога. Готовое — слой paving движка (код вида и хозяина, PaveRules.Code);
/// работа — укладка и разбор — сущность Pave на тайле (Position, Pave, Owner), её строят строители, как площадку.
/// Достроенное уходит в слой командой Layer, разбираемое лежит в слое, пока его не разберут.
/// </summary>
internal static class Paving
{
    /// <summary>Готовое покрытие тайла (код); нет слоя или покрытия — 0.</summary>
    public static int Laid(int x, int y) => S.Paving is { } layer ? (int)Tools.Layer(layer, 0, 0, x, y) : PaveRules.None;

    /// <summary>Лежит ли на тайле готовое покрытие этого вида (isPaved).</summary>
    public static bool IsPaved(string kind, int x, int y) => PaveRules.KindOf(Laid(x, y)) == kind;

    /// <summary>Местность тайла: 0 болото, 1 песок, 2 скала, 3 гора.</summary>
    public static int Terrain(int x, int y) => Tools.Terrain(x + 0.5, y + 0.5, out var info) ? info.Terrain : 3;

    public static bool Sand(int x, int y) => Terrain(x, y) == 1;

    /// <summary>Работа над покрытием на тайле — укладка или разбор (paving.at); нет — 0.</summary>
    public static uint At(Call call, int x, int y)
    {
        foreach (var entity in Tools.Near(x + 0.5, y + 0.5, 0.8, S.Pave))
            if (call.Has(entity, S.Pave) && call.At(entity) == (x, y))
                return entity;
        if (call.Writes)
            foreach (var born in call.Stage.Spawned)
                if (call.Has(born, S.Pave) && call.At(born) == (x, y))
                    return born;
        return 0;
    }

    /// <summary>Цена тайла покрытия (paveCost): дорога по болоту — мост.</summary>
    public static double Cost(string kind, int x, int y) => PaveRules.Cost(kind, kind == PaveRules.Road && Terrain(x, y) == 0);

    /// <summary>Кладёт готовое покрытие в слой.</summary>
    public static void Lay(Call call, int x, int y, int code)
    {
        if (S.Paving is { } layer)
            call.Commands.Layer(layer, 0, 0, x, y, 1, 1, code);
    }

    /// <summary>
    /// Можно ли игроку заложить покрытие в тайл (canPave): на карте, не в чужой зоне, не у подножия обрыва, исследовано;
    /// работы на тайле нет, готовое — разве что своё другого вида (прежнее действует, пока новое не готово). Фундамент —
    /// на скале или песке, дорога — где угодно, кроме гор. Дорогу — на свободное от зданий и площадок, фундамент — и под
    /// свои здания и площадки. Своей зоны покрытию не нужно.
    /// </summary>
    public static bool CanPave(Call call, uint player, string kind, int x, int y)
    {
        if (!PaveRules.Known(kind) || !Placement.InBounds(x, y))
            return false;
        var terrain = Terrain(x, y);
        if (kind == PaveRules.Foundation ? terrain is not (1 or 2) : terrain == 3)
            return false;
        // У подножия обрыва стоит его стенка: класть туда нельзя, как и строить.
        if ((Tools.Tile(x, y) & TileFlags.CliffFoot) != 0)
            return false;
        if (!Accounts.Researched(call, player, PaveRules.Topic(kind, terrain == 0)))
            return false;
        if (At(call, x, y) != 0)
            return false;
        var laid = Laid(x, y);
        if (laid != PaveRules.None && (PaveRules.KindOf(laid) == kind || PaveRules.OwnerOf(laid) != player))
            return false;
        var standing = Tools.Occupant(x, y);
        if (standing == 0)
            standing = Sites.SiteAt(call, x, y);
        if (standing != 0 && (kind != PaveRules.Foundation || !call.IsOwn(player, standing)))
            return false;
        return !Zones.InForeignZone(player, x, y, 1, 1);
    }

    /// <summary>
    /// Закладывает покрытие в тайлы и посылает строителей (orderPave): платят за тайл сразу; на что не хватило и куда
    /// нельзя — не кладут. Возвращает, сколько заложено.
    /// </summary>
    public static int Order(Call call, uint player, string kind, ReadOnlySpan<int> tiles, IReadOnlyList<uint> builders)
    {
        uint first = 0;
        var count = 0;
        for (var i = 0; i + 1 < tiles.Length && i < PaveRules.Limit * 2; i += 2)
        {
            var (x, y) = (tiles[i], tiles[i + 1]);
            if (!CanPave(call, player, kind, x, y) || !Accounts.Pay(call, player, Cost(kind, x, y)))
                continue;
            var entity = Spawn(call, player, kind, x, y);
            if (first == 0)
                first = entity;
            count++;
        }
        if (first != 0)
            Sites.AssignBuilders(call, player, first, builders);
        return count;
    }

    private static uint Spawn(Call call, uint player, string kind, int x, int y, double work = 0, bool remove = false)
    {
        var entity = call.Spawn();
        call.Add(entity, S.Position).Number(S.PositionX, x).Number(S.PositionY, y);
        call.Add(entity, S.Pave).Text(S.PaveKind, kind).Number(R.PaveWork, work).Flag(R.PaveDone, remove).Flag(S.PaveRemove, remove);
        call.Add(entity, S.Owner).Number(S.OwnerPlayer, player);
        return entity;
    }

    /// <summary>
    /// Разбирает строителями своё на тайлах (removePave): покрытие, трубы и здания, основание которых целиком в тайлах
    /// (главное — нет). Недостроенное покрытие, не начатые стройки и землеройные работы — сразу с возвратом; готовое
    /// покрытие разбирают без возврата, трубы и здания — с возвратом половины. Без своих строителей — ничего. assign — кого
    /// послать сразу.
    /// </summary>
    public static bool Remove(Call call, uint player, ReadOnlySpan<int> tiles, IReadOnlyList<uint> units, IReadOnlyList<uint> assign)
    {
        if (!units.Any(entity => call.IsOwn(player, entity) && call.Has(entity, S.Repair) && call.Has(entity, S.Unit)))
            return false;
        uint first = 0;
        var count = 0;
        var taken = new List<uint>();
        var covered = new HashSet<(int, int)>();
        for (var i = 0; i + 1 < tiles.Length && i < PaveRules.Limit * 2; i += 2)
            covered.Add((tiles[i], tiles[i + 1]));
        for (var i = 0; i + 1 < tiles.Length && i < PaveRules.Limit * 2; i += 2)
        {
            var (x, y) = (tiles[i], tiles[i + 1]);
            // Трубу снимают, даже если рамка задела её одним тайлом.
            if (PipeAt(call, player, x, y) is var pipe and not 0 && !taken.Contains(pipe))
                taken.Add(pipe);
            if (BuildingWithin(call, player, x, y, covered) is var building and not 0 && !taken.Contains(building))
                taken.Add(building);
            if (Earthworks.Cancel(call, player, x, y))
                count++;
            var entity = At(call, x, y);
            if (entity != 0)
            {
                if (!call.IsOwn(player, entity))
                    continue;
                count++;
                if (!call.Flag(entity, S.Pave, R.PaveDone))
                {
                    Accounts.Add(call, player, Cost(call.Text(entity, S.Pave, S.PaveKind) ?? PaveRules.Road, x, y));
                    call.Destroy(entity);
                    continue;
                }
                call.Set(entity, S.Pave).Flag(S.PaveRemove, true);
                if (first == 0)
                    first = entity;
                continue;
            }
            // Готовое покрытие разбирают строители: на тайле заводится работа разбора, покрытие действует, пока она идёт.
            var code = Laid(x, y);
            if (code == PaveRules.None || PaveRules.OwnerOf(code) != player)
                continue;
            count++;
            var kind = PaveRules.KindOf(code)!;
            var work = Spawn(call, player, kind, x, y, Sites.WorkTicks(Cost(kind, x, y)), true);
            if (first == 0)
                first = work;
        }
        foreach (var entity in taken)
        {
            count++;
            // Стройку отменяют: площадка исчезает сразу, недостроенное разбирают, как готовое.
            if (call.Has(entity, S.Site))
            {
                Sites.CancelBuild(call, player, entity);
                if (call.Alive(entity) && first == 0)
                    first = entity;
            }
            else if (Sites.Demolish(call, player, entity, []) && first == 0)
                first = entity;
        }
        if (first != 0)
            Sites.AssignBuilders(call, player, first, assign);
        return count > 0;
    }

    /// <summary>Своя труба или колодец в тайле — готовые, строящиеся или заложенные, не под разбором (pipeAt); нет — 0.</summary>
    public static uint PipeAt(Call call, uint player, int x, int y)
    {
        var entity = Tools.Occupant(x, y);
        if (entity == 0)
            entity = Sites.SiteAt(call, x, y);
        if (entity == 0 || !call.IsOwn(player, entity) || call.BuildingType(entity) is not { } type)
            return 0;
        var site = call.Has(entity, S.Site);
        return Factory.Building(type)?.Pipe is not null && !(site && call.Flag(entity, S.Site, R.SiteDemolish)) ? entity : 0;
    }

    /// <summary>
    /// Своё здание или стройка в тайле, основание которого целиком в covered (buildingWithin); главное и то, что уже
    /// разбирают, — не в счёт. Нет — 0.
    /// </summary>
    public static uint BuildingWithin(Call call, uint player, int x, int y, HashSet<(int, int)> covered)
    {
        var entity = Tools.Occupant(x, y);
        if (entity == 0)
            entity = Sites.SiteAt(call, x, y);
        if (entity == 0 || !call.IsOwn(player, entity) || Sites.Base(call, entity) is not { } at || at.Type == BuildRules.Core)
            return 0;
        if (call.Has(entity, S.Site) && call.Flag(entity, S.Site, R.SiteDemolish))
            return 0;
        var spec = Sites.Spec(at.Type);
        for (var ty = at.Y; ty < at.Y + spec.Height; ty++)
            for (var tx = at.X; tx < at.X + spec.Width; tx++)
                if (!covered.Contains((tx, ty)))
                    return 0;
        return entity;
    }

    /// <summary>Во сколько раз быстрее голой скалы строится здание здесь (buildSpeed): по самому медленному тайлу основания.</summary>
    public static double BuildSpeed(string type, int x, int y)
    {
        var spec = Sites.Spec(type);
        var speed = double.PositiveInfinity;
        for (var ty = y; ty < y + spec.Height; ty++)
            for (var tx = x; tx < x + spec.Width; tx++)
            {
                var code = Laid(tx, ty);
                speed = Math.Min(speed, PaveRules.BuildSpeed(code, PaveRules.IsFoundation(code) && Sand(tx, ty)));
            }
        return double.IsPositiveInfinity(speed) ? 1 : speed;
    }
}
