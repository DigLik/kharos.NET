using System.Buffers.Binary;
using Base.Host.Kernel;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Logistics;

/// <summary>
/// Транспортный луч (inventory.ts): груз между складом и юнитом переносит луч того, кто умеет отдавать или забирать, —
/// юниту достаточно встать в радиусе. Очереди нет: луч за тик работает с любым числом складов, каждый получает до rate
/// в секунду. Связи лучей за тик (Beam.links: сущность u32, тянет ли u8, номер Proto груза i32) — для клиента.
/// Подъезд на длину луча — инструмент движка Stand (approach в inventory.ts) с нормой поиска на тик.
/// </summary>
internal static class Beams
{
    /// <summary>Что сущность занимает на карте: основание здания или площадки, тайл дропа, круг юнита (shapeOf).</summary>
    public static (double X, double Y, double W, double H, double R)? Shape(View view, uint entity)
    {
        if (!view.Alive(entity))
            return null;
        var (x, y) = view.At(entity);
        if (double.IsNaN(x))
            return null;
        // Площадка, к которой ещё не приступили, — уже основание будущего здания: к ней подвозят материалы.
        if ((view.Building(entity) ?? view.SiteSpec(entity)) is { } spec)
            return (x, y, spec.Width, spec.Height, 0);
        if (view.IsDrop(entity))
            return (x, y, 1, 1, 0);
        return (x, y, 0, 0, view.IsUnit(entity) ? view.Radius(entity) : 0);
    }

    /// <summary>Расстояние между краями двух сущностей; вплотную и внахлёст — ноль (gapBetween).</summary>
    public static double Gap(View view, uint a, uint b)
    {
        if (Shape(view, a) is not { } one || Shape(view, b) is not { } two)
            return double.PositiveInfinity;
        var dx = Math.Max(0, Math.Max(one.X - two.X - two.W, two.X - one.X - one.W));
        var dy = Math.Max(0, Math.Max(one.Y - two.Y - two.H, two.Y - one.Y - one.H));
        return Math.Max(0, DetMath.Hypot(dx, dy) - one.R - two.R);
    }

    /// <summary>Чей луч перенесёт груз со склада from на склад to: отдающий у from или забирающий у to, из двух — длиннее (beamFor); нет — 0.</summary>
    public static uint BeamFor(View view, uint from, uint to)
    {
        if (!view.HasStock(from) || !view.HasStock(to))
            return 0;
        var giving = view.Beam(from) is { Give: true } give ? give.Radius : -1;
        var taking = view.Beam(to) is { Take: true } take ? take.Radius : -1;
        if (giving < 0 && taking < 0)
            return 0;
        return giving >= taking ? from : to;
    }

    /// <summary>Дотягивается ли луч, который перенёс бы груз со склада from на склад to (reaches).</summary>
    public static bool Reaches(View view, uint from, uint to)
    {
        var beam = BeamFor(view, from, to);
        return beam != 0 && Gap(view, from, to) <= view.Beam(beam)!.Value.Radius;
    }

    /// <summary>Переносит лучом до amount груза со склада from на склад to, если луч дотягивается; сколько перенесено (transfer).</summary>
    public static double Transfer(Pass pass, uint from, uint to, int good, double amount = double.PositiveInfinity)
    {
        var view = pass.V;
        var carrier = BeamFor(view, from, to);
        if (carrier == 0 || good < 0)
            return 0;
        var beam = view.Beam(carrier)!.Value;
        if (Gap(view, from, to) > beam.Radius)
            return 0;
        var moved = Math.Min(Math.Min(amount, beam.Rate * Core.Step), Math.Min(view.Amount(from, good), view.Room(to, good)));
        if (moved <= 0)
            return 0;
        var partner = carrier == from ? to : from;
        var pulling = carrier == to;
        if (!pass.Links.TryGetValue(carrier, out var links))
            pass.Links[carrier] = links = [];
        if (!links.Exists(link => link.Partner == partner && link.Pulling == pulling))
            links.Add((partner, pulling, good));
        view.Put(to, good, view.Take(from, good, moved));
        return moved;
    }

    /// <summary>Связи лучей носителя — в байты Beam.links.</summary>
    public static byte[] Pack(IReadOnlyList<(uint Partner, bool Pulling, int Good)> links)
    {
        var bytes = new byte[links.Count * 9];
        for (var i = 0; i < links.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 9), links[i].Partner);
            bytes[i * 9 + 4] = links[i].Pulling ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 9 + 5), Cargo.Proto(links[i].Good));
        }
        return bytes;
    }

    /// <summary>Связи из байтов Beam.links.</summary>
    public static List<(uint Partner, bool Pulling, int Good)> Unpack(byte[]? bytes)
    {
        var list = new List<(uint, bool, int)>();
        if (bytes is null)
            return list;
        for (var at = 0; at + 9 <= bytes.Length; at += 9)
            list.Add((BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at)), bytes[at + 4] != 0, Cargo.Of(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 5)))));
        return list;
    }

    /// <summary>
    /// Лучи после тика (resetBeams): у носителей, с кем луч работал в этом вызове, — эти связи; у остальных лучей связи
    /// пустеют. beams — все сущности с лучом, links — их нынешние связи (пачкой).
    /// </summary>
    public static void Reset(Pass pass, ReadOnlySpan<uint> beams, byte[]?[] links)
    {
        for (var i = 0; i < beams.Length; i++)
        {
            var now = pass.Links.TryGetValue(beams[i], out var list) ? Pack(list) : [];
            if (!now.AsSpan().SequenceEqual(links[i] ?? []))
                pass.Call.Set(beams[i], S.Beam).Bytes(L.BeamLinks, now);
        }
        foreach (var (carrier, list) in pass.Links)
            if (beams.IndexOf(carrier) < 0 && pass.Call.Alive(carrier) && pass.Call.Has(carrier, S.Beam))
                pass.Call.Set(carrier, S.Beam).Bytes(L.BeamLinks, Pack(list));
    }

    /// <summary>Дописывает связи вызова к нынешним связям лучей (после haul: подвоз строителями).</summary>
    public static void Append(Pass pass)
    {
        foreach (var (carrier, list) in pass.Links)
        {
            var links = Unpack(pass.Call.Bytes(carrier, S.Beam, L.BeamLinks));
            var changed = false;
            foreach (var link in list)
                if (!links.Exists(other => other.Partner == link.Partner && other.Pulling == link.Pulling))
                {
                    links.Add(link);
                    changed = true;
                }
            if (changed)
                pass.Call.Set(carrier, S.Beam).Bytes(L.BeamLinks, Pack(links));
        }
    }

    // ===== Подъезд =====

    /// <summary>Номер нормы поиска подъезда грузовиков на тик (SEEK_TILES в hauling.ts).</summary>
    public const uint SeekPool = 0x5EEC;

    /// <summary>Сколько тайлов за тик вместе осматривают поиски подъезда всех грузовиков (SEEK_TILES = SEARCH_LIMIT).</summary>
    public const uint SeekTiles = 20000;

    /// <summary>Итог подъезда: путь положен или уже на месте, места нет (промах), норма вышла (не промах).</summary>
    public enum Approach
    {
        Found,
        Missed,
        Cut,
    }

    /// <summary>
    /// Подводит юнит к цели так, чтобы между ними дотянулся луч длиной radius (approach): место ищет движок (Stand), путь —
    /// в Path. pool — норма поиска на тик (0 — без нормы).
    /// </summary>
    public static Approach Seek(View view, uint entity, uint target, double radius, uint pool = 0, uint budget = 0)
    {
        if (entity == target || Shape(view, target) is not { } shape || !Tools.Alive(entity))
            return Approach.Missed;
        // Юнит — круг с центром в его месте; здание и дроп — прямоугольник от левого верхнего угла.
        var plan = Tools.Stand(entity, shape.X, shape.Y, shape.W, shape.H, shape.R, radius, pool, budget);
        switch (plan.Status)
        {
            case PlanStatus.Found:
                Moves.WritePath(view.Call, entity, plan.Points, plan.GoalX, plan.GoalY, false, plan.Roads);
                return Approach.Found;
            case PlanStatus.Here:
                return Approach.Found;
            case PlanStatus.Cut:
                return Approach.Cut;
            default:
                view.Call.Remove(entity, S.Path);
                return Approach.Missed;
        }
    }
}
