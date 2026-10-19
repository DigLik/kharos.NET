using System.Buffers.Binary;

namespace Base.Client;

/// <summary>
/// Что идёт по трубам (flowPass.ts): пачка известна клиенту целиком с отправки — путь узлами (Batch.path: левые верхние
/// тайлы узлов, i32 x, y подряд), тики отправки и прихода, — так что метка цвета груза едет сама, без вестей с хоста.
/// На подземном отрезке между колодцами и над зданиями метки не видно — только на трубе. Пачки видят хозяин и союзники.
/// Подземный отрезок своих колодцев — пунктир от колодца до парного (drawWells); граф сети клиенту не шлётся, поэтому
/// пары узнаются по путям пачек: два колодца подряд, не соседние.
/// </summary>
internal sealed class Flow(Fx fx, Paint paint)
{
    /// <summary>Сторона метки пачки в тайлах и толщина её тёмной обводки (MARK, OUTLINE).</summary>
    private const double Mark = 0.3, Outline = 0.08;

    private static readonly Rgb Ink = new(0.04f, 0.05f, 0.08f);

    private static readonly Rgb Tunnel = new(0.25f, 0.39f, 0.56f);

    private readonly List<(double X, double Y, bool Well, uint Node)> _points = [];
    private readonly HashSet<(uint A, uint B)> _wells = [];
    private readonly List<(uint A, uint B)> _lost = [];

    public void Draw()
    {
        var c = fx.C;
        Wells();
        foreach (var entity in c.Each(fx.Batch))
        {
            if (c.Get(entity, fx.Batch) is not { } batch)
                continue;
            var sent = batch.Number(fx.Batch["sent"]);
            var share = (fx.Clock - sent) / Math.Max(1, batch.Number(fx.Batch["arrive"]) - sent);
            if (share < 0 || share > 1)
                continue;
            if (!Points(batch.Bytes(fx.Batch["path"])))
                continue;
            // Длина пути по отрезкам: метка едет с одной скоростью по всей ломаной.
            double total = 0;
            for (var i = 1; i < _points.Count; i++)
                total += Fx.Hypot(_points[i].X - _points[i - 1].X, _points[i].Y - _points[i - 1].Y);
            var left = share * total;
            for (var i = 1; i < _points.Count; i++)
            {
                var (from, to) = (_points[i - 1], _points[i]);
                var length = Fx.Hypot(to.X - from.X, to.Y - from.Y);
                if (left > length && i < _points.Count - 1)
                {
                    left -= length;
                    continue;
                }
                // Под землёй — между двумя колодцами, не соседними, — пачки не видно.
                if (from.Well && to.Well && length > 1)
                    break;
                var part = length > 0 ? Math.Min(1, left / length) : 1;
                var x = from.X + (to.X - from.X) * part;
                var y = from.Y + (to.Y - from.Y) * part;
                // Груз видно только на трубе: над зданием метку не рисуют.
                if (paint.At(x, y) is not { } under || fx.BuildingOf(under)?.Spec.Pipe is null || !fx.Visible(x, y, 1))
                    break;
                var good = fx.GoodOf((int)batch.Number(fx.Batch["resource"]));
                var color = Rgb.Of(Icons.ColorOf(good >= 0 ? Icons.Goods[good] : ""));
                var outer = Mark + Outline * 2;
                fx.Box(x - outer / 2, y - outer / 2, outer, outer, Ink);
                fx.Box(x - Mark / 2, y - Mark / 2, Mark, Mark, color);
                break;
            }
        }
    }

    /// <summary>Середины узлов пути: здание — центр основания, труба — центр тайла; узла клиент не знает — false.</summary>
    private bool Points(byte[] path)
    {
        _points.Clear();
        for (var at = 0; at + 8 <= path.Length; at += 8)
        {
            var (x, y) = (BinaryPrimitives.ReadInt32LittleEndian(path.AsSpan(at)), BinaryPrimitives.ReadInt32LittleEndian(path.AsSpan(at + 4)));
            if (paint.At(x, y) is not { } node || fx.BuildingOf(node) is not { } kind || fx.C.Pose(node) is not { } base_)
                return false;
            _points.Add((base_.X + kind.Spec.Width / 2, base_.Y + kind.Spec.Height / 2, kind.Type == "well", node));
        }
        for (var i = 1; i < _points.Count; i++)
            if (_points[i - 1].Well && _points[i].Well && Fx.Hypot(_points[i].X - _points[i - 1].X, _points[i].Y - _points[i - 1].Y) > 1)
                _wells.Add((Math.Min(_points[i - 1].Node, _points[i].Node), Math.Max(_points[i - 1].Node, _points[i].Node)));
        return _points.Count >= 2;
    }

    /// <summary>Пунктир подземного отрезка: точка на каждом тайле, кроме самих колодцев.</summary>
    private void Wells()
    {
        var c = fx.C;
        _lost.Clear();
        foreach (var (a, b) in _wells)
        {
            if (fx.BuildingOf(a)?.Type != "well" || fx.BuildingOf(b)?.Type != "well")
            {
                _lost.Add((a, b));
                continue;
            }
            if (!fx.Mine(a) || fx.CenterOf(a) is not { } one || fx.CenterOf(b) is not { } two)
                continue;
            var length = Fx.Hypot(two.X - one.X, two.Y - one.Y);
            if (length <= 1.25 || (!fx.Visible(one.X, one.Y) && !fx.Visible(two.X, two.Y)))
                continue;
            var (dx, dy) = ((two.X - one.X) / length, (two.Y - one.Y) / length);
            var zoom = 1 / fx.Pixel;
            c.Draw.Dashes(one.X + dx * 0.75, one.Y + dy * 0.75, two.X - dx * 0.5, two.Y - dy * 0.5, Tunnel.Hex, (float)zoom, (float)Math.Max(1, 0.12 * zoom), 0.7f);
        }
        foreach (var pair in _lost)
            _wells.Remove(pair);
    }
}
