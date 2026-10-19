using Base.Shared.Data;
using Base.Shared.Orders;
using Base.Shared.Rules;
using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;
using MemoryPack;

namespace Base.Client;

/// <summary>Что покроет протянутая мышью укладка (PaveStroke): тайлы, можно ли положить на каждый, цена.</summary>
/// <param name="Tool">Инструмент: foundation, road, remove, pipe, wall, gate, well, peak, cliff.</param>
/// <param name="Tiles">Тайлы x, y подряд.</param>
/// <param name="Allowed">Ляжет ли тут покрытие (или снимется), по тайлу.</param>
/// <param name="Short">Класть можно, но кредитов на тайл уже не хватает, по тайлу.</param>
/// <param name="Cost">Цена всего, что ляжет; у снятия — ноль.</param>
internal sealed record PaveStroke(string Tool, int[] Tiles, bool[] Allowed, bool[] Short, double Cost);

/// <summary>
/// Постановка (placing.ts, cursorPass.ts, часть controls.ts): место под здание или под главное здание, которым развернётся
/// MCV, — призрак зелёной или красной рамкой, круг, которым новое здание расширит зону, границы зон; покрытие, труба и
/// стена — протяжка левой кнопкой от тайла к тайлу линией с изломом или прямоугольником. Левый щелчок закладывает
/// (режим остаётся — можно сразу следующее), правая кнопка и Esc отменяют. Режимы включают панели через <see cref="Modes"/>.
/// Альт или наведение на кнопку здания показывают границы зон и без режима.
/// </summary>
internal sealed class PlacementControl(BaseState state, PlaceWorld world)
{
    /// <summary>Сдвиг указателя, после которого нажатие — протяжка, а не щелчок, в пикселях.</summary>
    private const float ClickSlop = 4;

    // Цвета TS (cursorPass.ts): разрешённое, запрещённое, зона, снимаемое, не хватает кредитов, подземный отрезок.
    private const uint Allowed = 0x59ff73, Forbidden = 0xff4d40, Control = 0x4d99ff, Remove = 0xffa633, Short = 0xffd926, Link = 0x66bfff;
    private const float FillAlpha = 0.18f, BorderAlpha = 0.85f, FaintAlpha = 0.5f;

    /// <summary>Точка границы зоны: сторона в пикселях экрана.</summary>
    private const float ControlDash = 3;

    /// <summary>Призрак здания: белый и красный множитель цвета (GHOST и GHOST_FORBIDDEN в buildingsPass.ts), прозрачность.</summary>
    private const uint GhostAllowed = 0xffffff, GhostForbidden = 0xff3324;
    private const float GhostAlpha = 0.7f;

    private (float X, float Y)? _press;
    private bool _dragged;
    private (int X, int Y)? _paveFrom;

    // Границы зон: считаются заново, только когда сменились сами круги.
    private double[] _ownCircles = [], _foreignCircles = [];
    private double[] _ownDots = [], _foreignDots = [];

    private Parts P => state.P;

    /// <summary>Свои выбранные юниты (кому слать стройку).</summary>
    private uint[] Builders(ClientContext c) => [.. Shared.Selection(c).Where(c.Mine)];

    /// <summary>Выбран ли строитель: без него режим выбора места отменяется.</summary>
    private bool HasBuilder(ClientContext c) =>
        Shared.Selection(c).Any(entity => c.Mine(entity) && c.Has(entity, P.Unit) && state.KindOf(c, entity)?.Name == "builder");

    /// <summary>Пока выбираешь место, условия режима могут пропасть: нет строителя, MCV, кредитов — режим кончается.</summary>
    private void Validate(ClientContext c)
    {
        if (Modes.Placing(c) is not null && !HasBuilder(c))
            c.Shared.Remove(Modes.PlacingKey);
        if (Modes.Deploying(c) is { } mcv && (!Shared.Selection(c).Contains(mcv) || !c.Alive(mcv)))
            c.Shared.Remove(Modes.DeployingKey);
        if (Modes.Paving(c) is { } tool)
        {
            if (!HasBuilder(c))
                c.Shared.Remove(Modes.PavingKey);
            // Покрытие оплачивают сразу: нет кредитов даже на самый дешёвый тайл — укладка выключается.
            else if (tool == "foundation" && state.Credits(c) < PaveRules.FoundationCost || tool == "road" && state.Credits(c) < PaveRules.RoadCost)
                c.Shared.Remove(Modes.PavingKey);
        }
        if (!Modes.Active(c))
        {
            _press = null;
            _paveFrom = null;
            _dragged = false;
        }
    }

    /// <summary>Тайл под указателем; указатель вне окна или над панелью — null.</summary>
    private static (int X, int Y)? PointerTile(ClientContext c)
    {
        var input = c.Input;
        if (!input.Inside || input.OverUi || double.IsNaN(input.WorldX) || double.IsNaN(input.WorldY))
            return null;
        return ((int)Math.Floor(input.WorldX), (int)Math.Floor(input.WorldY));
    }

    /// <summary>Где встанет здание, которое игрок сейчас выбирает место: вид, левый верхний тайл, годится ли.</summary>
    public (string Type, BuildingSpec Spec, int X, int Y, bool Allowed)? PlacementOf(ClientContext c)
    {
        if (PointerTile(c) is not { } tile)
            return null;
        // Место для MCV — то же главное здание серединой под указателем; годится — где оно поместится.
        if (Modes.Deploying(c) is not null)
        {
            if (world.Spec(c, BuildRules.Core) is not { } core)
                return null;
            var (cx, cy) = (tile.X - (int)Math.Floor(core.Width / 2), tile.Y - (int)Math.Floor(core.Height / 2));
            return (BuildRules.Core, core, cx, cy, world.CanPlace(c, core, cx, cy) && !world.InForeignZone(c, c.Player, cx, cy, (int)core.Width, (int)core.Height));
        }
        if (Modes.Placing(c) is not { } type || world.Spec(c, type) is not { } spec)
            return null;
        var (x, y) = world.Where(c, spec, tile.X, tile.Y);
        return (type, spec, x, y, world.CanBuild(c, c.Player, spec, x, y));
    }

    // ------------------------------------------------------------------ протяжка покрытия

    /// <summary>
    /// Что покроет протянутая мышью укладка: от тайла, где зажали кнопку, до тайла под указателем. Фундамент и снятие —
    /// прямоугольником, дорога, труба и стена — линией с одним изломом: сначала вдоль длинной стороны, потом поперёк. Пока
    /// кнопку не зажали — один тайл под указателем. Больше <see cref="PaveRules.Limit"/> тайлов разом не кладут.
    /// </summary>
    public PaveStroke? StrokeOf(ClientContext c, string tool, (int X, int Y) from, (int X, int Y) tile)
    {
        if (tool == "well")
            return WellStroke(c, from, tile);
        var tiles = new List<int>();
        if (tool is "road" or "pipe" or "wall" or "gate")
        {
            var horizontal = Math.Abs(tile.X - from.X) >= Math.Abs(tile.Y - from.Y);
            var corner = horizontal ? (X: tile.X, Y: from.Y) : (X: from.X, Y: tile.Y);
            void Walk((int X, int Y) a, (int X, int Y) b, bool skipFirst)
            {
                int stepX = Math.Sign(b.X - a.X), stepY = Math.Sign(b.Y - a.Y);
                var length = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
                for (var i = skipFirst ? 1 : 0; i <= length; i++)
                {
                    tiles.Add(a.X + stepX * i);
                    tiles.Add(a.Y + stepY * i);
                }
            }
            Walk(from, corner, false);
            Walk(corner, tile, true);
        }
        else
            for (var y = Math.Min(from.Y, tile.Y); y <= Math.Max(from.Y, tile.Y); y++)
                for (var x = Math.Min(from.X, tile.X); x <= Math.Max(from.X, tile.X); x++)
                {
                    tiles.Add(x);
                    tiles.Add(y);
                }
        if (tiles.Count > PaveRules.Limit * 2)
            tiles.RemoveRange(PaveRules.Limit * 2, tiles.Count - PaveRules.Limit * 2);
        var count = tiles.Count / 2;
        var allowed = new bool[count];
        var shortage = new bool[count];
        double cost = 0;
        var player = c.Player;
        if (tool is "peak" or "cliff")
        {
            // Землеройные работы хост собирает из рамки сам (digsIn): здесь рамка подсвечена целиком.
            Array.Fill(allowed, true);
            return new PaveStroke(tool, [.. tiles], allowed, shortage, 0);
        }
        if (tool is "pipe" or "wall" or "gate")
        {
            // Труба и стена — стройки: их оплачивают по ходу, поэтому ложатся и без кредитов.
            if (world.Spec(c, tool) is not { } spec)
                return null;
            var taken = new HashSet<(int, int)>();
            for (var i = 0; i < count; i++)
            {
                int x = tiles[i * 2], y = tiles[i * 2 + 1];
                var ok = !taken.Contains((x, y)) && (tool == "pipe" ? PipeFits(c, player, spec, x, y) : world.CanBuild(c, player, spec, x, y));
                if (ok)
                {
                    taken.Add((x, y));
                    cost += spec.Cost;
                }
                allowed[i] = ok;
            }
            return new PaveStroke(tool, [.. tiles], allowed, shortage, cost);
        }
        var credits = state.Credits(c);
        var covered = new HashSet<(int, int)>();
        if (tool == "remove")
            for (var i = 0; i < count; i++)
                covered.Add((tiles[i * 2], tiles[i * 2 + 1]));
        for (var i = 0; i < count; i++)
        {
            int x = tiles[i * 2], y = tiles[i * 2 + 1];
            if (tool == "remove")
            {
                // Разбирают своё покрытие — недостроенное или готовое, — трубы и здания, целиком попавшие в рамку.
                allowed[i] = OwnPave(c, player, x, y) || OwnPipeAt(c, player, x, y) != 0 || BuildingWithin(c, player, x, y, covered) != 0;
                continue;
            }
            double? price = world.CanPave(c, player, tool, x, y) ? PaveRules.Cost(tool, tool == PaveRules.Road && world.TerrainAt(x, y) == 0) : null;
            var affordable = price is { } p && cost + p <= credits;
            allowed[i] = affordable;
            shortage[i] = price is not null && !affordable;
            if (affordable)
                cost += price!.Value;
        }
        return new PaveStroke(tool, [.. tiles], allowed, shortage, cost);
    }

    private bool PipeFits(ClientContext c, uint player, BuildingSpec spec, int x, int y) =>
        (world.CanPlace(c, spec, x, y) || OwnPipeAt(c, player, x, y) != 0) && !world.InForeignZone(c, player, x, y, 1, 1);

    /// <summary>Своя труба или колодец в тайле — готовые или заложенные; нет — 0.</summary>
    private uint OwnPipeAt(ClientContext c, uint player, int x, int y)
    {
        var entity = Tools.Occupant(x, y);
        if (entity == 0)
            entity = world.SiteAt(c, x, y, 1, 1);
        return entity != 0 && c.Mine(entity) && world.KindOf(c, entity) is { } kind && kind.Spec.Pipe is not null ? entity : 0;
    }

    /// <summary>Своё покрытие на тайле: готовое или в работе.</summary>
    private bool OwnPave(ClientContext c, uint player, int x, int y)
    {
        if (world.PaveAt(c, x, y) is var work and not 0)
            return c.Mine(work);
        var code = world.Laid(c, x, y);
        return code != PaveRules.None && PaveRules.OwnerOf(code) == player;
    }

    /// <summary>Своё здание или стройка в тайле, основание которого целиком в covered; главное — не в счёт. Нет — 0.</summary>
    private uint BuildingWithin(ClientContext c, uint player, int x, int y, HashSet<(int, int)> covered)
    {
        var entity = Tools.Occupant(x, y);
        if (entity == 0)
            entity = world.SiteAt(c, x, y, 1, 1);
        if (entity == 0 || !c.Mine(entity) || world.KindOf(c, entity) is not { } kind || kind.Type == BuildRules.Core || c.Pose(entity) is not { } at)
            return 0;
        for (var ty = (int)Math.Floor(at.Y); ty < at.Y + kind.Spec.Height; ty++)
            for (var tx = (int)Math.Floor(at.X); tx < at.X + kind.Spec.Width; tx++)
                if (!covered.Contains((tx, ty)))
                    return 0;
        return entity;
    }

    /// <summary>
    /// Колодцы: от тайла, где зажали кнопку, по прямой вдоль длинной стороны протяжки, не дальше UndergroundReach, — пара.
    /// Без протяжки — один колодец под указателем. Протяжка от своего колодца закладывает только второй.
    /// </summary>
    private PaveStroke? WellStroke(ClientContext c, (int X, int Y) from, (int X, int Y) tile)
    {
        if (world.Spec(c, "well") is not { } spec)
            return null;
        var horizontal = Math.Abs(tile.X - from.X) >= Math.Abs(tile.Y - from.Y);
        var along = horizontal ? tile.X - from.X : tile.Y - from.Y;
        var length = Math.Min(Math.Abs(along), BuildRules.UndergroundReach) * Math.Sign(along);
        var end = horizontal ? (X: from.X + length, Y: from.Y) : (X: from.X, Y: from.Y + length);
        int[] tiles = length != 0 ? [from.X, from.Y, end.X, end.Y] : [from.X, from.Y];
        var count = tiles.Length / 2;
        var kept = new bool[count];
        var fresh = 0;
        var fits = new bool[count];
        for (var i = 0; i < count; i++)
        {
            int x = tiles[i * 2], y = tiles[i * 2 + 1];
            // Конец, где свой колодец уже стоит, не закладывают: от него тянут второй.
            var entity = tiles.Length == 4 ? OwnPipeAt(c, c.Player, x, y) : 0;
            kept[i] = entity != 0 && world.KindOf(c, entity)?.Type == "well";
            if (!kept[i])
                fresh++;
            fits[i] = PipeFits(c, c.Player, spec, x, y) || kept[i];
        }
        var single = tiles.Length == 2;
        var pair = tiles.Length == 4 && (tiles[0] == tiles[2] || tiles[1] == tiles[3]) && Math.Abs(tiles[2] - tiles[0]) + Math.Abs(tiles[3] - tiles[1]) is >= 2 and <= BuildRules.UndergroundReach;
        var valid = (single || pair) && fresh > 0;
        return new PaveStroke("well", tiles, [.. fits.Select(ok => ok && valid)], new bool[count], valid ? spec.Cost * fresh : 0);
    }

    // ------------------------------------------------------------------ кадр

    /// <summary>
    /// Раз в кадр. Возвращает, держит ли постановка мышь: пока включён режим, выделение и приказы точкой молчат (и в кадр,
    /// когда режим только что отменили правой кнопкой).
    /// </summary>
    public bool Update(ClientContext c)
    {
        Validate(c);
        var input = c.Input;
        var zonesHeld = input.Alt || input.Keys.Contains("alt") || c.Shared.ContainsKey(Modes.ZonesHoverKey);
        if (!Modes.Active(c))
        {
            if (zonesHeld)
                DrawZones(c);
            return false;
        }
        var tile = PointerTile(c);

        // Esc: сначала брошенная протяжка, потом сам режим.
        if (input.Hits.Contains("escape"))
        {
            if (_press is not null || _paveFrom is not null)
                (_press, _paveFrom, _dragged) = (null, null, false);
            else
                Modes.Cancel(c);
            return true;
        }
        // Правая кнопка отменяет режим.
        if (input.Pressed(PointerButton.Right) && input.Inside && !input.OverUi)
        {
            Modes.Cancel(c);
            (_press, _paveFrom, _dragged) = (null, null, false);
            return true;
        }
        if (input.Pressed(PointerButton.Left) && tile is { } pressed)
        {
            _press = (input.X, input.Y);
            _dragged = false;
            _paveFrom = Modes.Paving(c) is not null ? pressed : null;
        }
        if (_press is { } press)
        {
            if (Math.Sqrt((input.X - press.X) * (input.X - press.X) + (input.Y - press.Y) * (input.Y - press.Y)) > ClickSlop)
                _dragged = true;
            if (input.Released(PointerButton.Left))
            {
                Release(c, tile);
                _press = null;
                _paveFrom = null;
            }
            else if (!input.Down(PointerButton.Left))
                (_press, _paveFrom) = (null, null);
        }

        var placement = PlacementOf(c);
        PaveStroke? stroke = null;
        if (Modes.Paving(c) is { } tool && tile is { } at)
            stroke = StrokeOf(c, tool, _paveFrom ?? at, at);
        DrawZones(c);
        if (placement is { } place)
            DrawPlacement(c, place);
        if (stroke is not null)
            DrawStroke(c, stroke);
        return true;
    }

    /// <summary>Левую кнопку отпустили: закладывают здание, разворачивают MCV или отдают протяжку.</summary>
    private void Release(ClientContext c, (int X, int Y)? tile)
    {
        var shift = c.Input.Shift;
        if (Modes.Paving(c) is { } tool)
        {
            if (tile is not { } at || _paveFrom is not { } from || StrokeOf(c, tool, from, at) is not { } stroke)
                return;
            SendStroke(c, stroke, shift);
            return;
        }
        if (_dragged)
            return;
        if (PlacementOf(c) is not { } placement)
            return;
        if (!placement.Allowed)
        {
            c.Sound("deny", 0.3f);
            return;
        }
        if (Modes.Deploying(c) is { } mcv)
        {
            c.Send(DeployOrder.Name, MemoryPackSerializer.Serialize(new DeployOrder { Unit = mcv, At = true, X = placement.X, Y = placement.Y }));
            Modes.Cancel(c);
        }
        else
            c.Send(BuildOrder.Name, MemoryPackSerializer.Serialize(new BuildOrder { Building = placement.Type, X = placement.X, Y = placement.Y, Builders = Builders(c), Queue = shift }));
        c.Sound("place", 0.35f);
    }

    /// <summary>Отдаёт хосту то, что можно положить из протяжки.</summary>
    private void SendStroke(ClientContext c, PaveStroke stroke, bool queue)
    {
        var tiles = new List<int>();
        for (var i = 0; i < stroke.Allowed.Length; i++)
            if (stroke.Allowed[i])
            {
                tiles.Add(stroke.Tiles[i * 2]);
                tiles.Add(stroke.Tiles[i * 2 + 1]);
            }
        if (tiles.Count == 0)
            return;
        var builders = Builders(c);
        var array = tiles.ToArray();
        switch (stroke.Tool)
        {
            case "peak" or "cliff":
                c.Send(DigOrder.Name, MemoryPackSerializer.Serialize(new DigOrder { Kind = stroke.Tool, Tiles = stroke.Tiles, Builders = builders, Queue = queue }));
                break;
            case "well":
                // Колодцы кладут парой целиком или никак.
                if (array.Length == stroke.Tiles.Length)
                    c.Send(WellsOrder.Name, MemoryPackSerializer.Serialize(new WellsOrder { Tiles = array, Builders = builders, Queue = queue }));
                break;
            case "remove":
                c.Send(UnpaveOrder.Name, MemoryPackSerializer.Serialize(new UnpaveOrder { Tiles = array, Builders = builders, Queue = queue }));
                break;
            case "pipe":
                c.Send(PipesOrder.Name, MemoryPackSerializer.Serialize(new PipesOrder { Tiles = array, Builders = builders, Queue = queue }));
                break;
            case "wall" or "gate":
                c.Send(WallsOrder.Name, MemoryPackSerializer.Serialize(new WallsOrder { Tiles = array, Builders = builders, Gate = stroke.Tool == "gate", Queue = queue }));
                break;
            default:
                c.Send(PaveOrder.Name, MemoryPackSerializer.Serialize(new PaveOrder { Kind = stroke.Tool, Tiles = array, Builders = builders, Queue = queue }));
                break;
        }
        c.Sound("place", 0.35f);
    }

    // ------------------------------------------------------------------ рисунок

    /// <summary>Рамка прямоугольника в один пиксель пиксель-арта (1/16 тайла).</summary>
    private static void Frame(ClientContext c, double left, double top, double width, double height, uint color, float alpha = BorderAlpha) =>
        c.Draw.Rect(left, top, left + width, top + height, color, (float)Math.Max(1, c.Camera.Zoom / 16), alpha);

    /// <summary>Залитый прямоугольник с рамкой.</summary>
    private static void Area(ClientContext c, double left, double top, double width, double height, uint color)
    {
        c.Draw.Rect(left, top, left + width, top + height, color, null, FillAlpha);
        Frame(c, left, top, width, height, color);
    }

    /// <summary>Призрак здания, его основание, круг расширения зоны.</summary>
    private void DrawPlacement(ClientContext c, (string Type, BuildingSpec Spec, int X, int Y, bool Allowed) place)
    {
        var proto = c.LookId("buildings/" + place.Type);
        if (proto >= 0)
            c.Draw.Sprite(proto, place.X, place.Y, alpha: GhostAlpha, layer: LookLayer.Building, tint: place.Allowed ? GhostAllowed : GhostForbidden);
        Frame(c, place.X, place.Y, place.Spec.Width, place.Spec.Height, place.Allowed ? Allowed : Forbidden);
        // Круг, которым готовое здание расширит зону: та его часть, что выходит за нынешние зоны.
        var radius = place.Spec.Isolated ? 0 : BuildRules.ZoneRadius(place.Spec);
        if (!place.Allowed || radius <= 0)
            return;
        double cx = place.X + place.Spec.Width / 2, cy = place.Y + place.Spec.Height / 2;
        var circles = world.CirclesOf(c, c.Player);
        // Нынешние круги, до которых новый дотягивается: только с ними сверяется каждая его точка.
        var near = new List<double>();
        for (var i = 0; i + 2 < circles.Count; i += 3)
            if (BuildRules.Hypot(circles[i] - cx, circles[i + 1] - cy) < circles[i + 2] + radius)
            {
                near.Add(circles[i]);
                near.Add(circles[i + 1]);
                near.Add(circles[i + 2]);
            }
        var dashes = ZoneOutline.DashesOf(radius);
        var added = new List<double>();
        for (var dash = 0; dash < dashes; dash++)
        {
            var angle = (double)dash / dashes * Math.PI * 2;
            double x = cx + Math.Cos(angle) * radius, y = cy + Math.Sin(angle) * radius;
            var covered = false;
            for (var i = 0; i + 2 < near.Count && !covered; i += 3)
                covered = BuildRules.Hypot(x - near[i], y - near[i + 1]) < near[i + 2] - 0.01;
            if (!covered)
            {
                added.Add(x);
                added.Add(y);
            }
        }
        Dots(c, added, Control, FaintAlpha);
    }

    /// <summary>Тайлы протянутого покрытия: зелёные, оранжевые у снятия, жёлтые — не хватает кредитов, красные — нельзя.</summary>
    private static void DrawStroke(ClientContext c, PaveStroke stroke)
    {
        var good = stroke.Tool == "remove" ? Remove : Allowed;
        if (stroke.Tool == "well" && stroke.Tiles.Length == 4)
        {
            // Пара колодцев: пунктир подземного отрезка между концами.
            var color = stroke.Allowed[0] && stroke.Allowed[1] ? Link : Forbidden;
            c.Draw.Dashes(stroke.Tiles[0] + 0.5, stroke.Tiles[1] + 0.5, stroke.Tiles[2] + 0.5, stroke.Tiles[3] + 0.5, color, 14, 3, BorderAlpha);
        }
        for (var i = 0; i < stroke.Allowed.Length; i++)
            Area(c, stroke.Tiles[i * 2], stroke.Tiles[i * 2 + 1], 1, 1, stroke.Allowed[i] ? good : stroke.Short[i] ? Short : Forbidden);
    }

    /// <summary>Точки границы зоны, что попали в кадр: квадраты <see cref="ControlDash"/> пикселей.</summary>
    private static void Dots(ClientContext c, IReadOnlyList<double> dots, uint color, float alpha)
    {
        var size = ControlDash / c.Camera.Zoom;
        var reachX = c.ScreenWidth / 2 / c.Camera.Zoom + size;
        var reachY = c.ScreenHeight / 2 / c.Camera.Zoom + size;
        for (var i = 0; i + 1 < dots.Count; i += 2)
        {
            double x = dots[i], y = dots[i + 1];
            if (Math.Abs(x - c.Camera.X) > reachX || Math.Abs(y - c.Camera.Y) > reachY)
                continue;
            c.Draw.Rect(x - size / 2, y - size / 2, x + size / 2, y + size / 2, color, null, alpha);
        }
    }

    /// <summary>Границы зон строительства: свои — где строить можно, чужие — где нельзя.</summary>
    private void DrawZones(ClientContext c)
    {
        var own = world.CirclesOf(c, c.Player);
        if (!own.SequenceEqual(_ownCircles))
        {
            _ownCircles = [.. own];
            _ownDots = ZoneOutline.Of(_ownCircles);
        }
        var foreign = world.ForeignCircles(c, c.Player);
        if (!foreign.SequenceEqual(_foreignCircles))
        {
            _foreignCircles = [.. foreign];
            _foreignDots = ZoneOutline.Of(_foreignCircles);
        }
        Dots(c, _ownDots, Control, BorderAlpha);
        Dots(c, _foreignDots, Forbidden, BorderAlpha);
    }
}
