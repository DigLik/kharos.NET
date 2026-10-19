using Kharos.Client.Looks;
using Kharos.Engine.Layers;
using Kharos.Engine.World;
using Kharos.Gpu;
using Kharos.Mod.Abi;
using RenderFrame = Kharos.Gpu.Frame;

namespace Kharos.Client.Game;

/// <summary>Спрайты кадра: сущности по обликам видов, предметы и клетки слоёв, свободные спрайты модов.</summary>
public sealed partial class ClientGame
{
    // Владелец — Д1 (docs/dotnet-n6.md, Н6.5): спрайты сущностей с Paint, свободные спрайты, предметы и клетки слоёв.

    private readonly Dictionary<uint, (float X, float Y, float Distance, double Seen)> _walked = [];
    private readonly List<(float Bottom, int At)> _buildings = [];
    private readonly List<float> _buildingSprites = [];
    private readonly List<uint> _forget = [];
    private readonly List<(int Layer, int Field, Look?[] Looks)> _itemLooks = [];
    private readonly List<(int Layer, int Channel, Look?[] Looks, bool Connect)> _cellLooks = [];

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
            if (item.Looks[i].Length > 0 && Content.FindProto(item.Looks[i]) is { } proto)
                looks[i] = _lookByProto[proto.Id];
        _itemLooks.Add((layer, field, looks));
    }

    /// <summary>
    /// Клетки слоя-сетки, которые рисуются обликами (покрытие, ModCellLook): слой, канал с номером вида, облики по
    /// номерам и стыковка с соседями. TODO(Д1): рисовать клетки (вариант — маска соседей при Connect); пока только
    /// разбор манифеста.
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
        for (var i = 0; i < looks.Length; i++)
            if (cell.Looks[i].Length > 0 && Content.FindProto(cell.Looks[i]) is { } proto)
                looks[i] = _lookByProto[proto.Id];
        _cellLooks.Add((layer, channel, looks, cell.Connect));
    }

    /// <summary>Клетки слоёв, что мод велел рисовать обликами (CellLooks, ABI 2).</summary>
    public int CellLookCount => _cellLooks.Count;

    /// <summary>
    /// Свободные спрайты модов (<see cref="ClientScene.Sprites"/>) и уточнения спрайтов сущностей
    /// (<see cref="ClientScene.Paints"/>) этого кадра. TODO(Д1): Paint — в <see cref="Sprites"/> (вариант, фаза, доля
    /// постройки, затемнение, «скрыть»), Sprite — в слои кадра; пока записи только разобраны.
    /// </summary>
    private int SceneSprites(RenderFrame frame) => 0;

    /// <summary>
    /// Спрайты видимых сущностей по слоям (build в game.rs): облик по виду, кадр по направлению и фазе (у ходовых — по
    /// пройденному пути), сторона по владельцу, тень, огни и лучи; здания — по нижнему краю. Возвращает, сколько спрайтов.
    /// </summary>
    private int Sprites(RenderFrame frame, double time)
    {
        if (Replica.World is not { } world)
            return 0;
        var view = frame.View;
        var (left, top, right, bottom) = view.Bounds(7);
        var player = Replica.Player;
        var fog = Replica.Welcome is { Fog: true };
        static float Snap(float value) => MathF.Round(value * RenderFrame.Texels, MidpointRounding.AwayFromZero) / RenderFrame.Texels;
        _buildings.Clear();
        _buildingSprites.Clear();
        Span<float> sprite = stackalloc float[RenderFrame.Sprite];
        var count = 0;
        var proto = world.Store(Builtin.Proto);
        var protoIds = proto.Column(0);
        foreach (var entity in world.Store(Builtin.Position).Entities)
        {
            var row = proto.RowOf(entity);
            if (row < 0 || LookOf((int)protoIds[row]) is not { } look)
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
            var (team, grey) = owner == 0 ? (3f, 1f) : owner == player ? (0f, 0f) : (1f, 0f);
            var cell = look.Cell(angle, phase);
            var uv = look.Cells[cell];
            var (w, h) = look.Tiles;
            var (spriteLeft, spriteTop) = (x + look.Offset.X - view.X, y + look.Offset.Y - view.Y);
            sprite[0] = spriteLeft;
            sprite[1] = spriteTop;
            sprite[2] = w;
            sprite[3] = h;
            sprite[4] = uv.U0;
            sprite[5] = uv.V0;
            sprite[6] = uv.U1;
            sprite[7] = uv.V1;
            sprite[8] = team;
            sprite[9] = look.Shadow.Offset;
            sprite[10] = look.Shadow.Opacity;
            sprite[11] = grey;
            if (layer == (int)LookLayer.Building)
            {
                _buildings.Add((y + look.Offset.Y + h, _buildingSprites.Count));
                foreach (var value in sprite) _buildingSprites.Add(value);
            }
            else
                foreach (var value in sprite) frame.SpriteLayers[Math.Min(layer, RenderFrame.Layers - 1)].Add(value);
            count++;

            var (cos, sin) = ((float)Math.Cos(angle), (float)Math.Sin(angle));
            var (forwardX, forwardY) = (cos / RenderFrame.Texels, sin / RenderFrame.Texels);
            foreach (var lamp in look.Spec.Lamps)
            {
                var (lx, ly) = (Snap(x + lamp.Along * forwardX - lamp.Across * forwardY), Snap(y + lamp.Along * forwardY + lamp.Across * forwardX));
                Push(frame.Spots, [lx, ly, lamp.Radius * 2, 1]);
                Push(frame.Blooms, [lx, ly, lamp.Radius, 1]);
            }
            if (look.Spec.Beam is { } beam)
                Push(frame.Beams, [Snap(x + beam.Along * forwardX), Snap(y + beam.Along * forwardY), beam.Length, beam.Level, cos, sin, beam.Near, beam.Spread]);
            // Огни кадра (здания): пятно в 16 радиусов лампы, ореол в 3 + 2 (lights.add в buildingsPass.ts).
            foreach (var light in look.FrameLights[cell])
            {
                if (light.Level <= Renderer.MinLevel)
                    continue;
                Push(frame.Spots, [x + light.X, y + light.Y, light.Size * 16, light.Level]);
                Push(frame.Blooms, [x + light.X, y + light.Y, light.Size * 3 + 2, light.Level]);
            }
        }
        if (_itemLooks.Count > 0 && Replica.Layers is { } layers)
            count += ItemSprites(frame, layers, sprite, (left, top, right, bottom), fog);
        if (_buildings.Count > 0)
        {
            _buildings.Sort((a, b) => a.Bottom.CompareTo(b.Bottom));
            var layer = frame.SpriteLayers[(int)LookLayer.Building];
            foreach (var (_, at) in _buildings)
                for (var i = 0; i < RenderFrame.Sprite; i++)
                    layer.Add(_buildingSprites[at + i]);
        }
        // Путь забывается через 2 с без показа.
        _forget.Clear();
        foreach (var (entity, walk) in _walked)
            if (time - walk.Seen >= 2)
                _forget.Add(entity);
        foreach (var entity in _forget)
            _walked.Remove(entity);
        return count;
    }

    /// <summary>
    /// Предметы слоёв, которые мод велел рисовать обликами (месторождения): место предмета — точка привязки облика;
    /// рисуются там, где разведано, как руда. Возвращает, сколько спрайтов.
    /// </summary>
    private int ItemSprites(RenderFrame frame, LayerSet layers, Span<float> sprite, (float Left, float Top, float Right, float Bottom) area, bool fog)
    {
        var count = 0;
        var view = frame.View;
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
                        var uv = look.Cells[look.Cell(0, 0)];
                        var (w, h) = look.Tiles;
                        sprite[0] = x + look.Offset.X - view.X;
                        sprite[1] = y + look.Offset.Y - view.Y;
                        sprite[2] = w;
                        sprite[3] = h;
                        sprite[4] = uv.U0;
                        sprite[5] = uv.V0;
                        sprite[6] = uv.U1;
                        sprite[7] = uv.V1;
                        sprite[8] = 3;
                        sprite[9] = look.Shadow.Offset;
                        sprite[10] = look.Shadow.Opacity;
                        sprite[11] = 1;
                        var target = frame.SpriteLayers[Math.Min((int)look.Spec.Layer, RenderFrame.Layers - 1)];
                        foreach (var value in sprite) target.Add(value);
                        count++;
                    }
        }
        return count;
    }
}
