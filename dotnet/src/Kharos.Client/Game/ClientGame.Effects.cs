using Kharos.Engine.Layers;
using Kharos.Gpu;
using Kharos.Protocol;
using RenderFrame = Kharos.Gpu.Frame;

namespace Kharos.Client.Game;

/// <summary>Эффекты кадра: частицы, следы на земле, туман войны, атмосфера, огни и лучи модов.</summary>
public sealed partial class ClientGame
{
    // Владелец — Д1 (docs/dotnet-n6.md, Н6.5): частицы, следы, туман, атмосфера, огни и лучи модов.

    /// <summary>С какой доли срока следы растворяются.</summary>
    private const float FadeFrom = 0.25f;

    /// <summary>Туман над тайлом, байты: не разведан, разведан, виден (fogPass.ts).</summary>
    private const byte FogHidden = 240, FogRemembered = 128, FogVisible = 0;

    private int _fogRevision = -1;
    private (float X, float Y) _drift;
    private (byte[] Alpha, int Width, int Height)? _fogImage;
    private int _fogImageRevision, _fogDelivered;

    /// <summary>Атмосфера кадра: свет, осадки, ветер и накопленный снос пелены.</summary>
    private void Atmosphere(RenderFrame frame, float dt)
    {
        var atmosphere = Scene.Atmosphere;
        frame.Light = Math.Clamp(atmosphere.Light, 0, 1);
        frame.Precipitation = Math.Clamp(atmosphere.Precipitation, 0, 1);
        frame.Wind = (atmosphere.WindX, atmosphere.WindY);
        // Снос копится, а не считается как ветер × время: смена ветра не дёргает пелену (precipitationPass.ts).
        _drift = (Euclid(_drift.X + atmosphere.WindX * (float)dt, Renderer.Window), Euclid(_drift.Y + atmosphere.WindY * (float)dt, Renderer.Window));
        frame.Drift = _drift;
    }

    /// <summary>
    /// Огни и лучи модов этого кадра (<see cref="ClientScene.Lights"/>, <see cref="ClientScene.Beams"/>). TODO(Д1):
    /// в Frame.Spots, Frame.Blooms и Frame.Beams; пока записи только разобраны.
    /// </summary>
    private void SceneLights(RenderFrame frame)
    {
    }

    /// <summary>Частицы: ход, снос ветром, отмирание; рисуются в слой поверх мира квадратами по текселю.</summary>
    private void StepParticles(float dt)
    {
        var (wx, wy) = (Scene.Atmosphere.WindX, Scene.Atmosphere.WindY);
        var particles = Scene.Particles;
        var kept = 0;
        for (var i = 0; i < particles.Count; i++)
        {
            var p = particles[i];
            p.Age += dt;
            var drift = p.Drift ? 1f : 0f;
            p.X += (p.Vx + wx * drift) * dt;
            p.Y += (p.Vy + wy * drift) * dt;
            if (p.Age >= p.Life)
                continue;
            particles[kept++] = p;
            var fade = MathF.Min(1 - p.Age / p.Life, 1);
            var size = p.Size / RenderFrame.Texels;
            var x = MathF.Floor(p.X * RenderFrame.Texels) / RenderFrame.Texels;
            var y = MathF.Floor(p.Y * RenderFrame.Texels) / RenderFrame.Texels;
            Scene.World.Rect(x, y, x + size, y + size, Rgba(p.Color, fade));
        }
        particles.RemoveRange(kept, particles.Count - kept);
    }

    /// <summary>
    /// Следы на земле: стареют, с <see cref="FadeFrom"/> срока растворяются (плавная ступень), вне обзора забываются;
    /// что в кадре — в frame.Decals (decalsPass.ts).
    /// </summary>
    private void Decals(float dt, RenderFrame frame)
    {
        var (left, top, right, bottom) = frame.View.Bounds(2);
        for (var node = Scene.Decals.First; node is not null;)
        {
            var next = node.Next;
            ref var decal = ref node.ValueRef;
            decal.Age += dt;
            if (decal.Age >= decal.Life || !Replica.Sees((int)MathF.Floor(decal.X2), (int)MathF.Floor(decal.Y2)))
            {
                Scene.Decals.Remove(node);
                node = next;
                continue;
            }
            if (decal.X2 >= left && decal.X2 <= right && decal.Y2 >= top && decal.Y2 <= bottom)
            {
                var share = MathF.Max((decal.Age / decal.Life - FadeFrom) / (1 - FadeFrom), 0);
                var remaining = 1 - share * share * (3 - 2 * share);
                Push(frame.Decals, [decal.X1, decal.Y1, decal.X2, decal.Y2, decal.Width, 0, 0, 0, 0, decal.R, decal.G, decal.B, decal.Opacity, remaining, decal.Density, decal.Seed]);
            }
            node = next;
        }
    }

    /// <summary>Туман войны: альфа на тайл с рамкой в тайл (неразведанной) — когда обзор поменялся.</summary>
    private void UpdateFog(RenderFrame frame)
    {
        if (Replica.Welcome is not { Fog: true, Map: { } map } welcome || Replica.Layers is not { } layers)
            return;
        frame.Fog = (map.Left - 1, map.Top - 1);
        if (_fogRevision == Replica.FogRevision)
            return;
        _fogRevision = Replica.FogRevision;
        int width = map.Width + 2, height = map.Height + 2;
        var alpha = new byte[width * height];
        alpha.AsSpan().Fill(FogHidden);
        var owner = LayerOwner.Player(welcome.Player);
        var sight = layers.Find(Fog.Sight);
        var explored = layers.Find(Fog.Explored);
        int fromX = map.Left >> 5, toX = (map.Right - 1) >> 5, fromY = map.Top >> 5, toY = (map.Bottom - 1) >> 5;
        for (var cy = fromY; cy <= toY; cy++)
            for (var cx = fromX; cx <= toX; cx++)
            {
                var seen = sight is { } s ? layers.ChunkBytes(s, owner, cx, cy) : [];
                var known = explored is { } e ? layers.ChunkBytes(e, owner, cx, cy) : [];
                if (seen.IsEmpty && known.IsEmpty)
                    continue;
                for (var ty = 0; ty < LayerSet.Chunk; ty++)
                {
                    var y = (cy << 5) + ty;
                    if (y < map.Top || y >= map.Bottom)
                        continue;
                    for (var tx = 0; tx < LayerSet.Chunk; tx++)
                    {
                        var x = (cx << 5) + tx;
                        if (x < map.Left || x >= map.Right)
                            continue;
                        var tile = ty * LayerSet.Chunk + tx;
                        var value = !seen.IsEmpty && seen[tile] != 0 ? FogVisible : !known.IsEmpty && known[tile] != 0 ? FogRemembered : FogHidden;
                        alpha[(y - map.Top + 1) * width + (x - map.Left + 1)] = value;
                    }
                }
            }
        frame.FogUpdate = (alpha, width, height);
        _fogImage = frame.FogUpdate;
        _fogImageRevision++;
    }
}
