using Kharos.Client.Looks;
using Kharos.Mod.Abi;

namespace Kharos.Client.Game;

/// <summary>
/// Облик из потока клиентского мода (kh_look, ClientAbi «Поток облика»): метаданные — в <see cref="LookSpec"/>, кадры —
/// команды холста, которые <see cref="LookSpec.Draw"/> проигрывает на <see cref="Canvas"/> клиента. Так кадр
/// совпадает с рисунком тех же вызовов на C# до байта.
/// </summary>
public static class LookStream
{
    /// <summary>Разбор потока; неверный — <see cref="FormatException"/>.</summary>
    public static LookSpec Decode(ReadOnlySpan<byte> stream)
    {
        var bytes = stream.ToArray();
        var r = new AbiReader(bytes);
        int width = r.U16(), height = r.U16();
        var flags = r.U8();
        (double, double)? origin = (flags & ClientAbi.LookOrigin) != 0 ? (r.F64(), r.F64()) : null;
        var anchor = (r.F64(), r.F64());
        int directions = Math.Max((int)r.U16(), 1), phases = Math.Max((int)r.U16(), 1);
        var fps = r.F32();
        float? step = (flags & ClientAbi.LookStep) != 0 ? r.F32() : null;
        var layer = (LookLayer)r.U8();
        if (layer is not (LookLayer.Floor or LookLayer.Ground or LookLayer.Building or LookLayer.Air))
            throw new FormatException($"облик: слой {(byte)layer}");
        (float, float)? shadow = (flags & ClientAbi.LookShadow) != 0 ? (r.F32(), r.F32()) : null;
        int icon = r.U16();
        var lamps = new Lamp[r.U16()];
        for (var i = 0; i < lamps.Length; i++)
            lamps[i] = new Lamp(r.F32(), r.F32(), r.F32());
        Beam? beam = (flags & ClientAbi.LookBeam) != 0 ? new Beam(r.F32(), r.F32(), r.F32(), r.F32(), r.F32()) : null;
        PortraitSpec? portrait = null;
        if ((flags & ClientAbi.LookPortrait) != 0)
        {
            int portraitWidth = r.U16(), portraitHeight = r.U16();
            (double, double)? portraitOrigin = r.U8() != 0 ? (r.F64(), r.F64()) : null;
            var at = Skip(ref r);
            portrait = new PortraitSpec(portraitWidth, portraitHeight, canvas => Replay(canvas, bytes, at)) { Origin = portraitOrigin };
        }
        var frames = new int[phases * directions];
        for (var i = 0; i < frames.Length; i++)
            frames[i] = Skip(ref r);
        if (!r.End)
            throw new FormatException("облик: лишние байты");
        if (width is 0 or > 1024 || height is 0 or > 1024)
            throw new FormatException($"облик: кадр {width} × {height}");
        return new LookSpec((canvas, angle, phase) =>
        {
            var direction = 0;
            if (directions > 1)
            {
                var turns = Math.Round(angle / Look.Turn * directions, MidpointRounding.AwayFromZero) % directions;
                direction = (int)(turns < 0 ? turns + directions : turns);
            }
            Replay(canvas, bytes, frames[phase % phases * directions + direction]);
        })
        {
            Width = width,
            Height = height,
            Origin = origin,
            Anchor = anchor,
            Directions = directions,
            Phases = phases,
            Fps = fps,
            Step = step,
            Layer = layer,
            Shadow = shadow,
            Memory = (flags & ClientAbi.LookMemory) != 0,
            Lamps = lamps,
            Beam = beam,
            Icon = icon,
            Portrait = portrait,
        };
    }

    /// <summary>Пропускает команды кадра до End; возвращает, где они начинались.</summary>
    private static int Skip(ref AbiReader r)
    {
        var start = r.Position;
        while (true)
        {
            var op = r.U8();
            var size = op switch
            {
                ClientAbi.CanvasEnd => -1,
                ClientAbi.CanvasRect => 36,
                ClientAbi.CanvasCircle => 28,
                ClientAbi.CanvasRing => 36,
                ClientAbi.CanvasLine => 44,
                ClientAbi.CanvasPoly => r.U16() * 16 + 4,
                ClientAbi.CanvasPixel => 20,
                ClientAbi.CanvasTranslate => 16,
                ClientAbi.CanvasLight => 32,
                _ => throw new FormatException($"облик: команда холста {op}"),
            };
            if (size < 0)
                return start;
            r.Take(size);
        }
    }

    /// <summary>Команды кадра с места at — на холст.</summary>
    public static void Replay(Canvas canvas, ReadOnlySpan<byte> bytes, int at)
    {
        var r = new AbiReader(bytes);
        r.Take(at);
        Span<double> small = stackalloc double[64];
        while (true)
        {
            switch (r.U8())
            {
                case ClientAbi.CanvasEnd:
                    return;
                case ClientAbi.CanvasRect:
                    canvas.Rect(r.F64(), r.F64(), r.F64(), r.F64(), r.U32());
                    break;
                case ClientAbi.CanvasCircle:
                    canvas.Circle(r.F64(), r.F64(), r.F64(), r.U32());
                    break;
                case ClientAbi.CanvasRing:
                    canvas.Ring(r.F64(), r.F64(), r.F64(), r.F64(), r.U32());
                    break;
                case ClientAbi.CanvasLine:
                    canvas.Line(r.F64(), r.F64(), r.F64(), r.F64(), r.F64(), r.U32());
                    break;
                case ClientAbi.CanvasPoly:
                {
                    var count = r.U16() * 2;
                    var coords = count <= small.Length ? small[..count] : new double[count];
                    for (var i = 0; i < count; i++)
                        coords[i] = r.F64();
                    canvas.Poly(coords, r.U32());
                    break;
                }
                case ClientAbi.CanvasPixel:
                    canvas.Pixel(r.F64(), r.F64(), r.U32());
                    break;
                case ClientAbi.CanvasTranslate:
                    canvas.Translate(r.F64(), r.F64());
                    break;
                case ClientAbi.CanvasLight:
                    canvas.Light(r.F64(), r.F64(), r.F64(), r.F64());
                    break;
                default:
                    throw new FormatException("облик: команда холста");
            }
        }
    }
}
