using Kharos.Client.Looks;
using Kharos.Client.Ui;
using Kharos.Engine.Layers;
using Kharos.Engine.Terrain;
using Kharos.Engine.World;
using Kharos.Gpu;
using Kharos.Mod.Abi;
using RenderFrame = Kharos.Gpu.Frame;

namespace Kharos.Client.Game;

/// <summary>Спрайты кадра: сущности по обликам видов, предметы и клетки слоёв, свободные спрайты модов.</summary>
public sealed partial class ClientGame
{
    // Владелец — Д1 (docs/dotnet-n6.md, Н6.5): спрайты сущностей с Paint, свободные спрайты, предметы и клетки слоёв.

    /// <summary>Недостроенное — чертёж, сквозь который видно землю (BLUEPRINT в buildingsPass.ts; альфа меньше половины — лучам не преграда).</summary>
    public static readonly (float R, float G, float B, float A) Blueprint = (0.3f, 0.6f, 1, 0.4f);

    /// <summary>Выключенное — темнее (SWITCHED_OFF в buildingsPass.ts); затемнение Paint ведёт к нему от обычного цвета.</summary>
    public static readonly (float R, float G, float B, float A) SwitchedOff = (0.55f, 0.57f, 0.62f, 1);

    private static readonly (float R, float G, float B, float A) Plain = (1, 1, 1, 1);

    private readonly Dictionary<uint, (float X, float Y, float Distance, double Seen)> _walked = [];
    // Здания и спрайты слоя зданий: сортируются по нижнему краю (нижние — поверх), спрайтов у здания бывает два (чертёж и готовая часть).
    private readonly List<(float Bottom, int At, int Count)> _buildings = [];
    private readonly List<float> _buildingSprites = [];
    private readonly List<uint> _forget = [];
    private readonly List<(int Layer, int Field, Look?[] Looks)> _itemLooks = [];
    private readonly List<(int Layer, int Channel, Look?[] Looks, Look?[] OverSwamp, bool Connect)> _cellLooks = [];

    /// <summary>Предметы слоя, которые рисуются обликами видов: слой, поле с номером вида и облики по номерам.</summary>
    private void ResolveItemLook(string mod, ModItemLook item)
    {
        var layers = Replica.Layers!;
        if (layers.Find(item.Layer) is not { } layer || layers.Spec(layer).IsGrid)
        {
            Report($"мод {mod}: предметы слоя {item.Layer} — нет такого слоя предметов");
            return;
        }
        var fields = layers.Spec(layer).Fields;
        var field = -1;
        for (var i = 0; i < fields.Count; i++)
            if (fields[i].Name == item.Field)
                field = i;
        if (field < 0)
        {
            Report($"мод {mod}: у слоя {item.Layer} нет поля {item.Field}");
            return;
        }
        var looks = new Look?[item.Looks.Length];
        for (var i = 0; i < looks.Length; i++)
            if (item.Looks[i].Length > 0)
                looks[i] = LookOf(item.Looks[i]);
        _itemLooks.Add((layer, field, looks));
    }

    /// <summary>
    /// Клетки слоя-сетки, которые рисуются обликами (покрытие, ModCellLook): слой, канал с номером вида, облики по
    /// номерам и стыковка с соседями (<see cref="CellSprites"/>).
    /// </summary>
    private void ResolveCellLook(string mod, ModCellLook cell)
    {
        var layers = Replica.Layers!;
        if (layers.Find(cell.Layer) is not { } layer || !layers.Spec(layer).IsGrid)
        {
            Report($"мод {mod}: клетки слоя {cell.Layer} — нет такого слоя-сетки");
            return;
        }
        if (!int.TryParse(cell.Field, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var channel) || channel >= layers.Spec(layer).Channels.Count)
        {
            Report($"мод {mod}: у слоя {cell.Layer} нет канала {cell.Field}");
            return;
        }
        var looks = new Look?[cell.Looks.Length];
        // Дорога над болотом — мост (pavingPass.ts): облик «<раздел>/road» на болоте заменяет «<раздел>/bridge», если он есть.
        var overSwamp = new Look?[cell.Looks.Length];
        for (var i = 0; i < looks.Length; i++)
        {
            var name = cell.Looks[i];
            if (name.Length == 0)
                continue;
            looks[i] = LookOf(name);
            if (looks[i] is not null && name.EndsWith("/road", StringComparison.Ordinal))
                overSwamp[i] = LookOf(name[..^"road".Length] + "bridge");
        }
        _cellLooks.Add((layer, channel, looks, overSwamp, cell.Connect));
    }

    private readonly Dictionary<uint, long> _heads = [];

    /// <summary>
    /// Союзы из реплики: компонент движка Team { player, head } на сущности счёта игрока (уходит всем) — союзны разные
    /// игроки с одним head ≥ 0 (Teams хоста).
    /// </summary>
    private void ReadTeams(Kharos.Engine.World.GameWorld world)
    {
        _heads.Clear();
        var store = world.Store(Builtin.Team);
        if (store.Count == 0)
            return;
        var players = store.Column(0);
        var heads = store.Column(1);
        for (var row = 0; row < players.Length; row++)
            if (players[row] >= 1 && players[row] <= uint.MaxValue && heads[row] >= 0)
                _heads[(uint)players[row]] = (long)heads[row];
    }

    /// <summary>Глава союза игрока; −1 — ни с кем.</summary>
    private long HeadOf(uint player) => _heads.TryGetValue(player, out var head) ? head : -1;

    /// <summary>Союзник ли игроку партии other (по Team реплики).</summary>
    public bool Allied(uint other)
    {
        if (Replica.World is not { } world || other == 0 || other == Replica.Player)
            return false;
        ReadTeams(world);
        return HeadOf(Replica.Player) is >= 0 and var head && HeadOf(other) == head;
    }

    /// <summary>Клетки слоёв, что мод велел рисовать обликами (CellLooks, ABI 2).</summary>
    public int CellLookCount => _cellLooks.Count;

    /// <summary>
    /// Кадр облика: вариант variant, фаза frame среди Frames (с LookWorking — сначала покой, затем «в работе»),
    /// направление по angle. Без Paint и вариантов — то же, что <see cref="Look.Cell(double, int)"/>.
    /// </summary>
    public static int CellOf(Look look, double angle, int frame, int variant) =>
        (Math.Clamp(variant, 0, look.Variants - 1) * look.Frames + (int)((uint)frame % (uint)look.Frames)) * look.Directions + look.Cell(angle, 0);

    /// <summary>Множитель цвета с предумноженной альфой из rgba записи (r — младший байт) и прозрачности.</summary>
    private static (float R, float G, float B, float A) Premultiply(uint rgba, float alpha)
    {
        var a = Math.Clamp((rgba >> 24) / 255f * alpha, 0, 1);
        return ((rgba & 0xff) / 255f * a, (rgba >> 8 & 0xff) / 255f * a, (rgba >> 16 & 0xff) / 255f * a, a);
    }

    private static (float R, float G, float B, float A) Mix((float R, float G, float B, float A) a, (float R, float G, float B, float A) b, float t) =>
        (a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);

    /// <summary>16 чисел спрайта (Frame.Sprite) в sprite.</summary>
    private static void Fill(Span<float> sprite, float left, float top, float width, float height, Uv uv, float team, (float Offset, float Opacity) shadow, float grey,
        (float R, float G, float B, float A) tint)
    {
        sprite[0] = left;
        sprite[1] = top;
        sprite[2] = width;
        sprite[3] = height;
        sprite[4] = uv.U0;
        sprite[5] = uv.V0;
        sprite[6] = uv.U1;
        sprite[7] = uv.V1;
        sprite[8] = team;
        sprite[9] = shadow.Offset;
        sprite[10] = shadow.Opacity;
        sprite[11] = grey;
        sprite[12] = tint.R;
        sprite[13] = tint.G;
        sprite[14] = tint.B;
        sprite[15] = tint.A;
    }

    /// <summary>Спрайт в слой layer; в слое зданий — в очередь здания bottom (<see cref="FlushBuildings"/>).</summary>
    private void Emit(RenderFrame frame, int layer, ReadOnlySpan<float> sprite, float bottom, bool join = false)
    {
        if (layer == (int)LookLayer.Building)
        {
            if (join && _buildings.Count > 0)
                _buildings[^1] = _buildings[^1] with { Count = _buildings[^1].Count + 1 };
            else
                _buildings.Add((bottom, _buildingSprites.Count, 1));
            foreach (var value in sprite) _buildingSprites.Add(value);
        }
        else
            frame.SpriteLayers[Math.Clamp(layer, 0, RenderFrame.Layers - 1)].AddRange(sprite);
    }

    /// <summary>
    /// Облик в кадр: точка привязки (x, y) в тайлах мира, кадр cell, масштаб, сторона и серость, множитель цвета; built &lt; 1 —
    /// «растёт снизу» (buildingsPass.ts): поверх чертежа — нижние строки спрайта целым числом пикселей, без тени.
    /// Возвращает, сколько спрайтов.
    /// </summary>
    private int Put(RenderFrame frame, Span<float> sprite, Look look, int cell, float x, float y, float scale, int layer, float team, float grey,
        (float R, float G, float B, float A) tint, float built = 1)
    {
        var view = frame.View;
        var uv = look.Cells[cell];
        var (w, h) = (look.Tiles.Width * scale, look.Tiles.Height * scale);
        var (left, top) = (x + look.Offset.X * scale - view.X, y + look.Offset.Y * scale - view.Y);
        var bottom = y + (look.Offset.Y + look.Tiles.Height) * scale;
        if (built >= 1)
        {
            Fill(sprite, left, top, w, h, uv, team, (look.Shadow.Offset * scale, look.Shadow.Opacity), grey, tint);
            Emit(frame, layer, sprite, bottom);
            return 1;
        }
        Fill(sprite, left, top, w, h, uv, team, (0, 0), grey, Blueprint);
        Emit(frame, layer, sprite, bottom);
        var rows = (int)MathF.Round(look.Height * scale);
        var hidden = rows > 0 ? (rows - MathF.Floor(rows * Math.Max(built, 0))) / rows : 1;
        if (hidden >= 1)
            return 1;
        var part = new Uv(uv.U0, uv.V0 + (uv.V1 - uv.V0) * hidden, uv.U1, uv.V1);
        Fill(sprite, left, top + h * hidden, w, h * (1 - hidden), part, team, (0, 0), grey, Plain);
        Emit(frame, layer, sprite, bottom, join: true);
        return 2;
    }

    /// <summary>
    /// Свободные спрайты модов (<see cref="ClientScene.Sprites"/>, запись Sprite): облик вида, вариант, фаза, направление,
    /// масштаб, прозрачность, сторона, слой и множитель цвета. Затем — слой зданий целиком, по нижнему краю (свободные
    /// спрайты слоя зданий — призраки — встают в тот же порядок). Возвращает, сколько спрайтов.
    /// </summary>
    private int SceneSprites(RenderFrame frame)
    {
        Span<float> sprite = stackalloc float[RenderFrame.Sprite];
        var count = 0;
        foreach (var s in Scene.Sprites)
        {
            if (LookOf(s.Proto) is not { } look || !(s.Scale > 0) || !(s.Alpha > 0))
                continue;
            var tint = Premultiply(s.Tint, s.Alpha);
            if (tint.A <= 0)
                continue;
            var team = Math.Min((int)s.Team, RenderFrame.Teams - 1);
            count += Put(frame, sprite, look, CellOf(look, s.Angle, s.Frame, s.Variant), s.X, s.Y, s.Scale, s.Layer, team, team == 3 ? 1 : 0, tint);
        }
        FlushBuildings(frame);
        return count;
    }

    /// <summary>Слой зданий в кадр: нижние рисуются позже и перекрывают верхние.</summary>
    private void FlushBuildings(RenderFrame frame)
    {
        if (_buildings.Count > 0)
        {
            // При равном крае — в порядке появления (чертёж и готовая часть здания — одной записью).
            _buildings.Sort(static (a, b) => a.Bottom != b.Bottom ? a.Bottom.CompareTo(b.Bottom) : a.At.CompareTo(b.At));
            var layer = frame.SpriteLayers[(int)LookLayer.Building];
            foreach (var (_, at, n) in _buildings)
                for (var i = 0; i < RenderFrame.Sprite * n; i++)
                    layer.Add(_buildingSprites[at + i]);
        }
        _buildings.Clear();
        _buildingSprites.Clear();
    }

    /// <summary>
    /// Спрайты видимых сущностей по слоям (build в game.rs): облик по виду, кадр по направлению и фазе (у ходовых — по
    /// пройденному пути), сторона по владельцу, тень, огни и лучи; здания — по нижнему краю. Paint мода уточняет вариант,
    /// фазу, долю постройки и затемнение или прячет сущность. Возвращает, сколько спрайтов.
    /// </summary>
    private int Sprites(RenderFrame frame, double time)
    {
        _buildings.Clear();
        _buildingSprites.Clear();
        if (Replica.World is not { } world)
            return 0;
        var view = frame.View;
        var (left, top, right, bottom) = view.Bounds(7);
        var player = Replica.Player;
        var fog = Replica.Welcome is { Fog: true };
        static float Snap(float value) => MathF.Round(value * RenderFrame.Texels, MidpointRounding.AwayFromZero) / RenderFrame.Texels;
        Span<float> sprite = stackalloc float[RenderFrame.Sprite];
        var count = 0;
        if (_cellLooks.Count > 0 && Replica.Layers is { } cellLayers)
            count += CellSprites(frame, cellLayers, sprite, fog);
        var proto = world.Store(Builtin.Proto);
        var protoIds = proto.Column(0);
        var paints = Scene.Paints;
        ReadTeams(world);
        var myHead = HeadOf(player);
        foreach (var entity in world.Store(Builtin.Position).Entities)
        {
            var row = proto.RowOf(entity);
            if (row < 0 || LookOf((int)protoIds[row]) is not { } look)
                continue;
            ScenePaint paint = default;
            if (paints.Count > 0 && paints.TryGetValue(entity, out paint) && paint.Hide)
                continue;
            if (!Poses.TryGet(world, entity, out var px, out var py, out var angle))
                continue;
            var (x, y) = ((float)px, (float)py);
            if (x < left || x > right || y < top || y > bottom)
                continue;
            var owner = world.TryNumber(entity, Builtin.Owner, 0, out var number) ? (uint)number : 0;
            // Чужое вне обзора не видно (руда — где разведано).
            if (owner != player && fog && !(look.Spec.Memory ? Replica.Explored((int)MathF.Floor(x), (int)MathF.Floor(y)) : Replica.Sees((int)MathF.Floor(x), (int)MathF.Floor(y))))
                continue;
            // Турель — слоем выше своего носителя.
            var layer = (int)look.Spec.Layer;
            if (world.TryNumber(entity, Builtin.Mount, 0, out var parent) && parent >= 1)
                layer = (world.TryNumber((uint)parent, Builtin.Proto, 0, out var parentProto) && LookOf((int)parentProto) is { } carrier ? (int)carrier.Spec.Layer : 0) + 1;
            var phase = 0;
            if (look.Phases > 1)
            {
                if (look.Spec.Step is { } step)
                {
                    (float X, float Y, float Distance, double Seen) walk = _walked.TryGetValue(entity, out var known) ? known : (x, y, 0f, time);
                    var moved = MathF.Sqrt((x - walk.X) * (x - walk.X) + (y - walk.Y) * (y - walk.Y));
                    var distance = walk.Distance + (moved < 1 ? moved : 0);
                    _walked[entity] = (x, y, distance, time);
                    phase = (int)(distance / step) % look.Phases;
                }
                else
                    phase = (int)(time * look.Spec.Fps) % look.Phases;
            }
            // Сторона палитры: своя, союзная (тот же глава Team, что у игрока), чужая; ничья — серая.
            var (team, grey) = owner == 0 ? (3f, 1f) : owner == player ? (0f, 0f) : myHead >= 0 && HeadOf(owner) == myHead ? (2f, 0f) : (1f, 0f);
            var cell = paint.Mask == 0
                ? look.Cell(angle, phase)
                : CellOf(look, angle, paint.Has(ClientAbi.PaintFrame) ? paint.Frame : phase, paint.Has(ClientAbi.PaintVariant) ? paint.Variant : 0);
            var built = paint.Has(ClientAbi.PaintBuilt) ? Math.Clamp(float.IsNaN(paint.Built) ? 1 : paint.Built, 0, 1) : 1;
            var dim = paint.Has(ClientAbi.PaintDim) ? Math.Clamp(float.IsNaN(paint.Dim) ? 0 : paint.Dim, 0, 1) : 0;
            count += Put(frame, sprite, look, cell, x, y, 1, layer, team, grey, dim > 0 ? Mix(Plain, SwitchedOff, dim) : Plain, built);
            // Недостроенное не светит; выключенное гаснет вместе с затемнением.
            var glow = built < 1 ? 0 : 1 - dim;
            if (glow <= 0)
                continue;

            var (cos, sin) = ((float)Math.Cos(angle), (float)Math.Sin(angle));
            var (forwardX, forwardY) = (cos / RenderFrame.Texels, sin / RenderFrame.Texels);
            foreach (var lamp in look.Spec.Lamps)
            {
                var (lx, ly) = (Snap(x + lamp.Along * forwardX - lamp.Across * forwardY), Snap(y + lamp.Along * forwardY + lamp.Across * forwardX));
                Push(frame.Spots, [lx, ly, lamp.Radius * 2, glow]);
                Push(frame.Blooms, [lx, ly, lamp.Radius, glow]);
            }
            if (look.Spec.Beam is { } beam)
                Push(frame.Beams, [Snap(x + beam.Along * forwardX), Snap(y + beam.Along * forwardY), beam.Length, beam.Level * glow, cos, sin, beam.Near, beam.Spread]);
            // Огни кадра (здания): пятно в 16 радиусов лампы, ореол в 3 + 2 (lights.add в buildingsPass.ts).
            foreach (var light in look.FrameLights[cell])
            {
                var level = light.Level * glow;
                if (level <= Renderer.MinLevel)
                    continue;
                Push(frame.Spots, [x + light.X, y + light.Y, light.Size * 16, level]);
                Push(frame.Blooms, [x + light.X, y + light.Y, light.Size * 3 + 2, level]);
            }
        }
        if (_itemLooks.Count > 0 && Replica.Layers is { } layers)
            count += ItemSprites(frame, layers, sprite, (left, top, right, bottom), fog);
        // Путь забывается через 2 с без показа.
        _forget.Clear();
        foreach (var (entity, walk) in _walked)
            if (time - walk.Seen >= 2)
                _forget.Add(entity);
        foreach (var entity in _forget)
            _walked.Remove(entity);
        // Слой зданий уходит в кадр в SceneSprites — вместе со свободными спрайтами слоя.
        return count;
    }

    /// <summary>
    /// Предметы слоёв, которые мод велел рисовать обликами (месторождения): место предмета — точка привязки облика;
    /// рисуются там, где разведано, как руда, в своих цветах (depositsPass.ts: ничьи, но не серые). Возвращает, сколько спрайтов.
    /// </summary>
    private int ItemSprites(RenderFrame frame, LayerSet layers, Span<float> sprite, (float Left, float Top, float Right, float Bottom) area, bool fog)
    {
        var count = 0;
        foreach (var (layer, field, looks) in _itemLooks)
        {
            var size = (double)layers.Spec(layer).Cell;
            for (var cy = (int)Math.Floor(area.Top / size); cy <= (int)Math.Floor(area.Bottom / size); cy++)
                for (var cx = (int)Math.Floor(area.Left / size); cx <= (int)Math.Floor(area.Right / size); cx++)
                    foreach (var item in layers.Items(layer, LayerOwner.World, (cx, cy)))
                    {
                        var kind = (int)item.Fields[field];
                        if ((uint)kind >= (uint)looks.Length || looks[kind] is not { } look)
                            continue;
                        var (x, y) = ((float)item.X, (float)item.Y);
                        if (x < area.Left || x > area.Right || y < area.Top || y > area.Bottom)
                            continue;
                        var (tx, ty) = ((int)MathF.Floor(x), (int)MathF.Floor(y));
                        if (fog && !(look.Spec.Memory ? Replica.Explored(tx, ty) : Replica.Sees(tx, ty)))
                            continue;
                        count += Put(frame, sprite, look, look.Cell(0, 0), x, y, 1, (int)look.Spec.Layer, 3, 0, Plain);
                    }
        }
        return count;
    }

    /// <summary>
    /// Клетки слоёв-сеток обликами (CellLooks, покрытие — pavingPass.ts): значение канала тайла — номер облика; со
    /// стыковкой вариант — маска соседей с тем же обликом (1 север, 2 восток, 4 юг, 8 запад), без неё — вариант 0. Клетка
    /// ложится кадром облика с серединой в середине тайла (точка привязки облика не в счёт) в <see cref="RenderFrame.Cells"/> — на землю, под следы и спрайты; под
    /// туманом — где разведано. Облик «…/road» на болоте рисуется «…/bridge» того же раздела (мост, pavingPass.ts); стыковка
    /// — по облику из канала, без моста. Возвращает, сколько клеток.
    /// </summary>
    private int CellSprites(RenderFrame frame, LayerSet layers, Span<float> sprite, bool fog)
    {
        var view = frame.View;
        var (left, top, right, bottom) = view.Bounds(1);
        int fromX = (int)MathF.Floor(left), toX = (int)MathF.Ceiling(right), fromY = (int)MathF.Floor(top), toY = (int)MathF.Ceiling(bottom);
        var count = 0;
        foreach (var (layer, channel, looks, overSwamp, connect) in _cellLooks)
        {
            var owner = layers.Spec(layer).Scope == LayerScope.Player ? LayerOwner.Player(Replica.Player) : LayerOwner.World;
            Look? At(int x, int y)
            {
                var value = layers.Get(layer, owner, x, y, channel);
                var index = (int)value;
                return value > 0 && index < looks.Length ? looks[index] : null;
            }
            for (var y = fromY; y <= toY; y++)
                for (var x = fromX; x <= toX; x++)
                {
                    if (At(x, y) is not { } look)
                        continue;
                    if (fog && !Replica.Explored(x, y))
                        continue;
                    var mask = !connect ? 0
                        : (At(x, y - 1) == look ? 1 : 0) | (At(x + 1, y) == look ? 2 : 0) | (At(x, y + 1) == look ? 4 : 0) | (At(x - 1, y) == look ? 8 : 0);
                    // Стыкуются клетки одного облика (вида покрытия), чьи бы они ни были; мост — та же дорога.
                    var index = (int)layers.Get(layer, owner, x, y, channel);
                    if (overSwamp[index] is { } bridge && Land is { } land && land.TerrainAt(x, y) == TerrainType.Swamp)
                        look = bridge;
                    var uv = look.Cells[CellOf(look, 0, 0, mask)];
                    Fill(sprite, x + (1 - look.Tiles.Width) / 2 - view.X, y + (1 - look.Tiles.Height) / 2 - view.Y, look.Tiles.Width, look.Tiles.Height, uv, 0, (0, 0), 0, Plain);
                    frame.Cells.AddRange(sprite);
                    count++;
                }
        }
        return count;
    }
}
