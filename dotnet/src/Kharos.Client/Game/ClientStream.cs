using Kharos.Audio;
using Kharos.Audio.Graph;
using Kharos.Client.Ui;
using Kharos.Mod.Abi;
using Gui = Kharos.Client.Ui.Gui;

namespace Kharos.Client.Game;

/// <summary>
/// Разбор выхода кадра клиентского мода (ClientAbi «Выход кадра») в сцену: записи интерфейса — вызовами
/// <see cref="Gui"/> в том же порядке, рисунки — в слой поверх мира, звук — в голоса мода, приказы, камера, атмосфера.
/// Незакрытые узлы и подсказки мода закрываются в конце его потока: чужие панели они не ломают.
/// </summary>
public static class ClientStream
{
    /// <summary>Применяет выход мода к сцене. Неверная запись — <see cref="FormatException"/> (что успело — применено).</summary>
    public static void Apply(ReadOnlySpan<byte> output, ClientScene scene, ModSounds? sounds = null, Listener listener = default)
    {
        var gui = scene.Gui;
        var r = new AbiReader(output);
        var depth = 0;
        var tips = new Stack<int>();
        try
        {
            while (!r.End)
            {
                var op = r.U8();
                switch (op)
                {
                    case ClientAbi.OpOpen:
                        gui.Open(ReadStyle(ref r));
                        depth++;
                        break;
                    case ClientAbi.OpClose:
                        if (depth > 0)
                        {
                            gui.Close();
                            depth--;
                        }
                        break;
                    case ClientAbi.OpText:
                    {
                        var text = r.Str();
                        var style = ReadText(ref r);
                        if (FollowedByIcon(r))
                            Rich(ref r, scene, new Run(text, style), false);
                        else
                            gui.Text(text, style);
                        break;
                    }
                    case ClientAbi.OpImage:
                    {
                        var kind = r.U8();
                        var (w, h) = (r.F32(), r.F32());
                        gui.Image(new ImageSpec(kind == ClientAbi.ImageMinimap ? ImageKind.Minimap : ImageKind.Coin, w, h));
                        break;
                    }
                    case ClientAbi.OpImageLook:
                    {
                        var proto = r.I32();
                        var (w, h) = (r.F32(), r.F32());
                        var team = r.U8();
                        if (scene.LookOf(proto) is { } look)
                            gui.Image(look.Icon, w, h, team % Kharos.Gpu.Frame.Teams);
                        else
                            gui.Space(w, h);
                        break;
                    }
                    case ClientAbi.OpSpace:
                        gui.Space(r.F32(), r.F32(), r.F32());
                        break;
                    case ClientAbi.OpButton:
                    {
                        var id = r.Str();
                        var flags = r.U8();
                        var style = ReadStyle(ref r);
                        var skin = gui.ButtonSkin(id, style.Skin, (flags & ClientAbi.ButtonActive) != 0, (flags & ClientAbi.ButtonDisabled) != 0);
                        gui.Open(style with { Id = id, Skin = skin });
                        depth++;
                        break;
                    }
                    case ClientAbi.OpTipText:
                    {
                        var id = r.Str();
                        gui.Tip(id, r.Str());
                        break;
                    }
                    case ClientAbi.OpTipBegin:
                        gui.BeginTip();
                        tips.Push(depth);
                        depth = 0;
                        break;
                    case ClientAbi.OpTipEnd:
                    {
                        var anchor = ReadAnchor(ref r);
                        if (tips.Count > 0)
                        {
                            gui.EndTip(anchor);
                            depth = tips.Pop();
                        }
                        break;
                    }
                    case ClientAbi.OpField:
                    {
                        // Поле ввода с кареткой и выделением; правки уходят в Gui.FrameEdits и во вход кадра мода.
                        var id = r.Str();
                        var value = r.Str();
                        gui.Input(id, value, ReadStyle(ref r));
                        break;
                    }
                    case ClientAbi.OpFieldWith:
                    {
                        var id = r.Str();
                        var value = r.Str();
                        var style = ReadStyle(ref r);
                        var mask = r.U8();
                        var options = new FieldOptions
                        {
                            Digits = (mask & ClientAbi.FieldDigits) != 0,
                            Password = (mask & ClientAbi.FieldPassword) != 0,
                            Focus = (mask & ClientAbi.FieldFocus) != 0,
                            MaxLength = (mask & ClientAbi.FieldMaxLength) != 0 ? r.U16() : 0,
                            Placeholder = (mask & ClientAbi.FieldPlaceholder) != 0 ? r.Str() : null,
                        };
                        gui.Input(id, value, style, options);
                        break;
                    }
                    case ClientAbi.OpIcon:
                    {
                        var run = ReadIcon(ref r, scene);
                        if (NextIsText(r))
                            Rich(ref r, scene, run, true);
                        else if (run.Icon is { } icon)
                            gui.Image(icon, run.Size, run.Size, 0);
                        else
                            gui.Space(run.Size, run.Size);
                        break;
                    }
                    case ClientAbi.OpLine:
                    {
                        var (x1, y1, x2, y2) = (r.F32(), r.F32(), r.F32(), r.F32());
                        var color = r.U32();
                        var width = r.F32();
                        scene.World.Line(x1, y1, x2, y2, width / Zoom(scene), color);
                        scene.Recorder?.Invoke(new FrameRecord(op, null, color, [x1, y1, x2, y2, width]));
                        break;
                    }
                    case ClientAbi.OpCircle:
                    {
                        var (x, y, radius) = (r.F32(), r.F32(), r.F32());
                        var color = r.U32();
                        var width = r.F32();
                        var zoom = Zoom(scene);
                        var segments = (int)Math.Clamp(MathF.Sqrt(radius * zoom) * 4, 12, 64);
                        scene.World.Circle(x, y, radius, float.IsNaN(width) ? null : width / zoom, color, segments);
                        scene.Recorder?.Invoke(new FrameRecord(op, null, color, [x, y, radius, width]));
                        break;
                    }
                    case ClientAbi.OpRect:
                    {
                        var (x1, y1, x2, y2) = (r.F32(), r.F32(), r.F32(), r.F32());
                        var color = r.U32();
                        var width = r.F32();
                        float left = MathF.Min(x1, x2), right = MathF.Max(x1, x2), top = MathF.Min(y1, y2), bottom = MathF.Max(y1, y2);
                        if (float.IsNaN(width))
                            scene.World.Rect(left, top, right, bottom, color);
                        else
                        {
                            var w = width / Zoom(scene);
                            scene.World.Line(left, top, right, top, w, color);
                            scene.World.Line(right, top, right, bottom, w, color);
                            scene.World.Line(right, bottom, left, bottom, w, color);
                            scene.World.Line(left, bottom, left, top, w, color);
                        }
                        scene.Recorder?.Invoke(new FrameRecord(op, null, color, [x1, y1, x2, y2, width]));
                        break;
                    }
                    case ClientAbi.OpDashes:
                    {
                        var (x1, y1, x2, y2) = (r.F32(), r.F32(), r.F32(), r.F32());
                        var color = r.U32();
                        var (step, dot) = (r.F32(), r.F32());
                        var zoom = Zoom(scene);
                        // Не больше 4096 точек на запись: шаг меньше пикселя на длинной линии — не повод съесть кадр.
                        var length = MathF.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
                        if (step > 0 && float.IsFinite(length) && length / (step / zoom) <= 4096)
                            scene.World.Dashes(x1, y1, x2, y2, step / zoom, dot / zoom, color);
                        scene.Recorder?.Invoke(new FrameRecord(op, null, color, [x1, y1, x2, y2, step, dot]));
                        break;
                    }
                    case ClientAbi.OpDashedCircle:
                    {
                        var (x, y, radius) = (r.F32(), r.F32(), r.F32());
                        var color = r.U32();
                        var dashes = r.U16();
                        var dot = r.F32();
                        scene.World.DashedCircle(x, y, radius, dashes, dot / Zoom(scene), color);
                        scene.Recorder?.Invoke(new FrameRecord(op, null, color, [x, y, radius, dashes, dot]));
                        break;
                    }
                    case ClientAbi.OpGlow:
                        scene.World.Additive = r.U8() != 0;
                        break;
                    case ClientAbi.OpSprite:
                    {
                        var sprite = new SceneSprite(r.I32(), r.U16(), r.U16(), r.F32(), r.F32(), r.F32(), r.F32(), r.F32(), r.U8(), r.U8(), r.U32());
                        if (scene.Sprites.Count < ClientScene.MaxSprites)
                            scene.Sprites.Add(sprite);
                        break;
                    }
                    case ClientAbi.OpPaint:
                    {
                        var entity = r.U32();
                        var mask = r.U8();
                        var paint = new ScenePaint(mask,
                            (mask & ClientAbi.PaintVariant) != 0 ? r.U16() : 0,
                            (mask & ClientAbi.PaintFrame) != 0 ? r.U16() : 0,
                            (mask & ClientAbi.PaintBuilt) != 0 ? r.F32() : 1,
                            (mask & ClientAbi.PaintDim) != 0 ? r.F32() : 0);
                        scene.Paints[entity] = scene.Paints.TryGetValue(entity, out var earlier) ? earlier.Merge(paint) : paint;
                        break;
                    }
                    case ClientAbi.OpLight:
                    {
                        var light = new SceneLight(r.F32(), r.F32(), r.F32(), r.F32(), r.F32());
                        if (scene.Lights.Count < ClientScene.MaxLights)
                            scene.Lights.Add(light);
                        break;
                    }
                    case ClientAbi.OpBeam:
                    {
                        var beam = new SceneBeam(r.F32(), r.F32(), r.F32(), r.F32(), r.F32(), r.F32(), r.F32(), r.F32());
                        if (scene.Beams.Count < ClientScene.MaxLights)
                            scene.Beams.Add(beam);
                        break;
                    }
                    case ClientAbi.OpParticle:
                    {
                        var particle = new Particle(r.F32(), r.F32(), r.F32(), r.F32(), 0, 0, 0, 0, true);
                        particle.Life = r.F32();
                        particle.Color = r.U32();
                        particle.Size = r.F32();
                        particle.Drift = r.U8() != 0;
                        if (scene.Particles.Count < ClientScene.MaxParticles && particle.Life > 0)
                            scene.Particles.Add(particle);
                        break;
                    }
                    case ClientAbi.OpDecal:
                        scene.AddDecal(r.F32(), r.F32(), r.F32(), r.F32(), r.F32(), r.F32(), r.F32(), r.F32(), r.F32(), r.F32(), r.F32());
                        break;
                    case ClientAbi.OpPlay:
                    {
                        var name = r.Str();
                        var (volume, pan, rate) = (r.F32(), r.F32(), r.F32());
                        sounds?.Engine?.Play(name, volume, pan, rate);
                        scene.Recorder?.Invoke(new FrameRecord(op, name, 0, [volume, pan, rate]));
                        break;
                    }
                    case ClientAbi.OpSoundAt:
                    {
                        var name = r.Str();
                        var (x, y) = (r.F64(), r.F64());
                        var (volume, reach, detune) = (r.F32(), r.F32(), r.F32());
                        sounds?.Engine?.PlayAt(name, listener, x, y, volume, reach, detune);
                        scene.Recorder?.Invoke(new FrameRecord(op, name, 0, [(float)x, (float)y, volume, reach, detune]));
                        break;
                    }
                    case ClientAbi.OpLoop:
                    {
                        var name = r.Str();
                        var (volume, pan) = (r.F32(), r.F32());
                        sounds?.Engine?.Loop(name, volume, pan);
                        scene.Recorder?.Invoke(new FrameRecord(op, name, 0, [volume, pan]));
                        break;
                    }
                    case ClientAbi.OpVolume:
                    {
                        var bus = r.U8();
                        var volume = r.F32();
                        if (bus < Buses.Count)
                            sounds?.Engine?.SetVolume((Bus)bus, volume);
                        break;
                    }
                    case ClientAbi.OpVoice:
                        Voice(ref r, sounds, scene);
                        break;
                    case ClientAbi.OpAutomate:
                    {
                        var handle = r.U32();
                        var node = r.U16();
                        var (param, kind) = (r.U8(), r.U8());
                        var value = r.F32();
                        var at = r.F64();
                        var tau = r.F32();
                        if (sounds?[handle] is { } sound)
                        {
                            if (param > (byte)ParamId.Release || kind > (byte)AutomationKind.CancelAndHold)
                                throw new FormatException($"автоматизация: параметр {param}, вид {kind}");
                            try
                            {
                                sound.Automate(node, (ParamId)param, (AutomationKind)kind, value, at, tau);
                            }
                            catch (ArgumentException error)
                            {
                                scene.Errors.Add(error.Message);
                            }
                        }
                        break;
                    }
                    case ClientAbi.OpPlace:
                    {
                        var handle = r.U32();
                        var (gain, pan) = (r.F32(), r.F32());
                        sounds?[handle]?.Place(gain, pan);
                        break;
                    }
                    case ClientAbi.OpRelease:
                    {
                        var handle = r.U32();
                        var fade = r.F32();
                        sounds?[handle]?.Release(fade);
                        break;
                    }
                    case ClientAbi.OpSend:
                    {
                        var name = r.Str();
                        scene.Orders.Add((name, r.Blob().ToArray()));
                        break;
                    }
                    case ClientAbi.OpPrefs:
                    {
                        var key = r.Str();
                        scene.Prefs.Add((key, r.Blob().ToArray()));
                        break;
                    }
                    case ClientAbi.OpShell:
                    {
                        var action = r.U8();
                        scene.Shell.Add((action, r.Str()));
                        break;
                    }
                    case ClientAbi.OpCamera:
                    {
                        var (x, y, zoom) = (r.F64(), r.F64(), r.F64());
                        if (double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(zoom))
                            scene.Camera = new CameraState(x, y, Math.Clamp(zoom, 2, 256));
                        break;
                    }
                    case ClientAbi.OpAtmosphere:
                    {
                        var mask = r.U8();
                        var atmosphere = scene.Atmosphere;
                        if ((mask & ClientAbi.AtmosphereLight) != 0) atmosphere.Light = Finite(r.F32(), 1);
                        if ((mask & ClientAbi.AtmosphereWind) != 0) (atmosphere.WindX, atmosphere.WindY) = (Finite(r.F32(), 0), Finite(r.F32(), 0));
                        if ((mask & ClientAbi.AtmospherePrecipitation) != 0) atmosphere.Precipitation = Finite(r.F32(), 0);
                        scene.Atmosphere = atmosphere;
                        break;
                    }
                    case ClientAbi.OpPalette:
                    {
                        Span<uint> colors = stackalloc uint[12];
                        for (var i = 0; i < 12; i++)
                            colors[i] = r.U32();
                        scene.SetPalette(colors);
                        break;
                    }
                    default:
                        throw new FormatException($"выход кадра: запись 0x{op:x2} @{r.Position - 1}");
                }
            }
        }
        finally
        {
            // Свечение — до конца выхода этого мода: следующий начинает с обычных заливок.
            scene.World.Additive = false;
            // Мод бросил узлы открытыми (сбой посреди панели): закрыть, чтобы следующий мод рисовал свои панели.
            while (tips.Count > 0)
            {
                while (depth-- > 0)
                    gui.Close();
                gui.EndTip();
                depth = tips.Pop();
            }
            while (depth-- > 0)
                gui.Close();
        }
    }

    private static float Zoom(ClientScene scene) => (float)scene.Camera.Zoom;

    private static float Finite(float value, float fallback) => float.IsFinite(value) ? value : fallback;

    private static void Voice(ref AbiReader r, ModSounds? sounds, ClientScene scene)
    {
        var handle = r.U32();
        var source = r.U8();
        VoiceGraph? graph = null;
        string? kind = null;
        if (source == ClientAbi.VoiceGraph)
        {
            var bytes = r.Blob();
            if (sounds?.Engine is not null)
            {
                try
                {
                    graph = VoiceGraph.Decode(bytes);
                }
                catch (FormatException error)
                {
                    scene.Errors.Add($"голос {handle}: {error.Message}");
                }
            }
        }
        else if (source == ClientAbi.VoiceKind)
            kind = r.Str();
        else
            throw new FormatException($"голос: источник {source}");
        var bus = r.U8();
        var into = r.U32();
        var intoNode = r.U16();
        var options = new VoiceOptions
        {
            Bus = bus < Buses.Count ? (Bus)bus : Bus.World,
            Into = sounds?.Into(into, intoNode),
            At = r.F64(),
            Length = r.F32(),
            Note = r.F32(),
            Velocity = r.F32(),
        };
        scene.Recorder?.Invoke(new FrameRecord(ClientAbi.OpVoice, kind, handle, [bus, (float)options.At, options.Length, options.Note, options.Velocity]));
        if (sounds is null || (graph is null && kind is null))
            return;
        try
        {
            sounds.Start(handle, graph, kind, options);
        }
        catch (FormatException error)
        {
            scene.Errors.Add($"голос {handle}: {error.Message}");
        }
    }

    private static Run ReadIcon(ref AbiReader r, ClientScene scene)
    {
        var proto = r.I32();
        var cell = r.U16();
        var size = r.F32();
        if (scene.LookOf(proto) is { Cells.Length: > 0 } look)
            return Run.Glyph(cell < look.Cells.Length ? new Icon(look.Cells[cell], look.Width, look.Height) : look.Icon, size);
        return Run.Glyph(null, size);
    }

    /// <summary>Идёт ли следом запись Icon (r — сразу после тела записи).</summary>
    private static bool FollowedByIcon(AbiReader r) => !r.End && r.U8() == ClientAbi.OpIcon;

    private static bool NextIsText(AbiReader r) => !r.End && r.U8() == ClientAbi.OpText;

    /// <summary>
    /// Строка со значками: записи Text и Icon подряд собираются в один ряд. Текст входит в ряд, если перед ним значок
    /// или после него значок; остальные Text — отдельные строки. Первый кусок уже прочитан; width ряда — Wrap первого текста.
    /// </summary>
    private static void Rich(ref AbiReader r, ClientScene scene, Run first, bool afterIcon)
    {
        var runs = new List<Run> { first };
        var previousIcon = afterIcon;
        while (!r.End)
        {
            var look = r;
            var op = look.U8();
            if (op == ClientAbi.OpIcon)
            {
                r = look;
                runs.Add(ReadIcon(ref r, scene));
                previousIcon = true;
            }
            else if (op == ClientAbi.OpText)
            {
                var text = look.Str();
                var style = ReadText(ref look);
                if (!previousIcon && !FollowedByIcon(look)) break;
                r = look;
                runs.Add(new Run(text, style));
                previousIcon = false;
            }
            else break;
        }
        var width = runs.FirstOrDefault(run => run.Text is not null).Style.Wrap;
        scene.Gui.Rich(runs, width);
    }

    private static Style ReadStyle(ref AbiReader r)
    {
        var mask = r.U32();
        var s = new Style();
        if ((mask & ClientAbi.StyleId) != 0) s.Id = r.Str();
        if ((mask & ClientAbi.StyleDirection) != 0) s.Direction = Enum<Direction>(r.U8(), (byte)Direction.Stack);
        if ((mask & ClientAbi.StyleSelf) != 0) s.Self = (Enum<Align>(r.U8(), (byte)Align.Between), Enum<Align>(r.U8(), (byte)Align.Between));
        if ((mask & ClientAbi.StyleGap) != 0) s.Gap = r.F32();
        if ((mask & ClientAbi.StylePadding) != 0) s.Padding = new Padding(r.F32(), r.F32(), r.F32(), r.F32());
        if ((mask & ClientAbi.StyleAlign) != 0) s.Align = Enum<Align>(r.U8(), (byte)Align.Between);
        if ((mask & ClientAbi.StyleJustify) != 0) s.Justify = Enum<Align>(r.U8(), (byte)Align.Between);
        if ((mask & ClientAbi.StyleWidth) != 0) s.Width = r.F32();
        if ((mask & ClientAbi.StyleHeight) != 0) s.Height = r.F32();
        if ((mask & ClientAbi.StyleMinWidth) != 0) s.MinWidth = r.F32();
        if ((mask & ClientAbi.StyleMaxWidth) != 0) s.MaxWidth = r.F32();
        if ((mask & ClientAbi.StyleMinHeight) != 0) s.MinHeight = r.F32();
        if ((mask & ClientAbi.StyleMaxHeight) != 0) s.MaxHeight = r.F32();
        if ((mask & ClientAbi.StyleGrow) != 0) s.Grow = r.F32();
        if ((mask & ClientAbi.StyleShrink) != 0) s.Shrink = true;
        if ((mask & ClientAbi.StyleWrap) != 0) s.Wrap = true;
        if ((mask & ClientAbi.StyleSkin) != 0) s.Skin = Enum<Skin>(r.U8(), (byte)Skin.Danger);
        if ((mask & ClientAbi.StyleBackground) != 0) s.Background = Rgba(r.U32());
        if ((mask & ClientAbi.StyleOutline) != 0) s.Outline = Rgba(r.U32());
        if ((mask & ClientAbi.StyleAlpha) != 0) s.Alpha = r.F32();
        if ((mask & ClientAbi.StyleClip) != 0) s.Clip = true;
        if ((mask & ClientAbi.StyleScroll) != 0) s.Scroll = true;
        if ((mask & ClientAbi.StyleAnchor) != 0) s.Anchor = ReadAnchor(ref r) ?? default(Anchor);
        if ((mask & ClientAbi.StylePassive) != 0) s.Passive = true;
        if ((mask & ClientAbi.StyleScrollEnd) != 0) s.ScrollEnd = true;
        if ((mask & ClientAbi.StyleModal) != 0) s.Modal = true;
        if ((mask & ClientAbi.StyleKeepFocus) != 0) s.KeepFocus = true;
        return s;
    }

    /// <summary>Привязка: маска 0 — нет, 0x80 — пустая, иначе биты и f32 на каждый.</summary>
    private static Anchor? ReadAnchor(ref AbiReader r)
    {
        var mask = r.U8();
        if (mask == 0)
            return null;
        return new Anchor(
            (mask & ClientAbi.AnchorLeft) != 0 ? r.F32() : null,
            (mask & ClientAbi.AnchorRight) != 0 ? r.F32() : null,
            (mask & ClientAbi.AnchorTop) != 0 ? r.F32() : null,
            (mask & ClientAbi.AnchorBottom) != 0 ? r.F32() : null,
            (mask & ClientAbi.AnchorCenterX) != 0 ? r.F32() : null,
            (mask & ClientAbi.AnchorCenterY) != 0 ? r.F32() : null);
    }

    private static TextStyle ReadText(ref AbiReader r)
    {
        var mask = r.U8();
        var t = new TextStyle();
        if ((mask & ClientAbi.TextFont) != 0) t.Font = Enum<FontName>(r.U8(), (byte)FontName.Logo);
        if ((mask & ClientAbi.TextColor) != 0) t.Color = Rgba(r.U32());
        if ((mask & ClientAbi.TextShadow) != 0) t.Shadow = Rgba(r.U32());
        if ((mask & ClientAbi.TextLift) != 0) t.Lift = r.F32();
        if ((mask & ClientAbi.TextWrap) != 0) t.Wrap = r.F32();
        if ((mask & ClientAbi.TextEllipsis) != 0) t.Ellipsis = true;
        return t;
    }

    private static Color Rgba(uint value) => new((byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24));

    private static T Enum<T>(byte value, byte max) where T : struct, System.Enum =>
        value <= max ? System.Runtime.CompilerServices.Unsafe.As<byte, T>(ref value) : throw new FormatException($"{typeof(T).Name}: {value}");
}
