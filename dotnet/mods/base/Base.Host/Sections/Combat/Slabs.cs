using Base.Host.Kernel;
using Base.Shared.Rules;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Combat;

/// <summary>Следы боя в слое traces движка (traces.ts): виды по порядку TRACE_LIFE, кладутся командой Item.</summary>
internal static class Traces
{
    public const int Track = 0, Scar = 1, Burn = 2, Wreck = 3;

    /// <summary>Слой следов; у партии без него — null.</summary>
    public static LayerRef? Layer { get; set; }
}

/// <summary>
/// Наземный взрыв разбивает фундамент (breakSlabs): плиты, чьи центры в его радиусе, и ту, на которую он пришёлся; пуля —
/// нет. Готовая плита уходит со слоя paving, а с ней и работа над ней (Pave: укладка или разбор).
/// </summary>
internal static class Slabs
{
    /// <summary>Взрыв меньше этого — пуля: фундамент от неё не трескается.</summary>
    public const double SlabBlast = 0.5;

    public static void Break(Call call, double x, double y, double size)
    {
        if (size < SlabBlast || S.Paving is not { } layer)
            return;
        var radius = size / 2;
        var (left, top) = ((int)Math.Floor(x - radius), (int)Math.Floor(y - radius));
        var (right, bottom) = ((int)Math.Floor(x + radius), (int)Math.Floor(y + radius));
        var (w, h) = (right - left + 1, bottom - top + 1);
        var codes = new double[w * h];
        Tools.Layer(layer, 0, 0, left, top, w, h, codes);
        uint[]? works = null;
        for (var tileY = top; tileY <= bottom; tileY++)
            for (var tileX = left; tileX <= right; tileX++)
            {
                var hit = tileX == (int)Math.Floor(x) && tileY == (int)Math.Floor(y);
                var (dx, dy) = (tileX + 0.5 - x, tileY + 0.5 - y);
                if (!hit && Math.Sqrt(dx * dx + dy * dy) > radius)
                    continue;
                if (PaveRules.IsFoundation((int)codes[(tileY - top) * w + tileX - left]))
                    call.Commands.Layer(layer, 0, 0, tileX, tileY, 1, 1, PaveRules.None);
                works ??= Tools.Near(x, y, radius + 2, S.Pave);
                foreach (var work in works)
                    if (call.Alive(work) && call.At(work) == (tileX, tileY) && call.Text(work, S.Pave, S.PaveKind) == PaveRules.Foundation)
                        call.Destroy(work);
            }
    }
}
