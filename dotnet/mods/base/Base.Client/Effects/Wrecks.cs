using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Отметины и остовы со слоя следов движка (decalsPass.ts; слой traces — предметы id, tick, kind, entity, type, facing,
/// player, size; kind по порядку TRACE_LIFE: колея, гарь, прожог, остов). Гарь взрыва — пятно по размеру взрыва, от пули —
/// щербинки; прожог огня и лазера — маленькое пятно; остов — облик погибшего юнита по его повороту. На болоте отметин нет.
/// Живут свой срок и с четверти срока растворяются; видны только в обзоре (хост шлёт слой в обзоре). Точки колеи отдаются
/// <see cref="Traces"/>.
/// </summary>
internal sealed class Wrecks(Fx fx)
{
    private const int Scar = 1, Burn = 2, Wreck = 3;

    /// <summary>Сроки следов, секунды (TRACE_LIFE: track, scar, burn, wreck).</summary>
    private static readonly double[] Life = [90, 240, 240, 600];

    /// <summary>С какой доли срока следы растворяются (FADE_FROM).</summary>
    private const double FadeFrom = 0.25;

    /// <summary>Поперечники гари в пикселях, вариантов на размер, гари на тайл размера взрыва, порог щербинок.</summary>
    private static readonly int[] ScorchSizes = [8, 16, 24, 32, 40, 48];
    private const int Variants = 3, WreckDirections = 16, UnitFrame = 32;
    private const double ScorchScale = 1.5, PockSize = 0.3;

    /// <summary>Гарь просвечивает грунтом, остов непрозрачен (MARK_OPACITY).</summary>
    private const float MarkOpacity = 0.7f;

    private bool _broken;

    /// <summary>Выборка слоя следов: тик, правка слоёв, середина и радиус, с запасом <see cref="Spare"/> тайлов на ход камеры.</summary>
    private MapItem[]? _items;
    private ulong _itemsTick, _itemsRevision;
    private double _itemsX, _itemsY, _itemsReach;
    private const double Spare = 4;

    /// <summary>Облики гари по размеру (<see cref="ScorchSizes"/>) и остовов по виду: имя облика — строкой раз, а не каждый кадр.</summary>
    private int[]? _scorch;
    private int _pock = -1;
    private readonly Dictionary<string, int> _wreckLooks = new(StringComparer.Ordinal);
    private readonly List<(double X, double Y, double[] Fields)> _wrecks = [];

    /// <summary>Точки колеи (kind track) в кадре: их соединяет <see cref="Traces.Receive"/>.</summary>
    public List<(double X, double Y, double[] Fields)> Tracks { get; } = [];

    /// <summary>Слой следов читается: колею можно брать с него.</summary>
    public bool Works => !_broken && fx.Traces is not null;

    public void Draw()
    {
        if (_broken || fx.Traces is not { } layer)
            return;
        var c = fx.C;
        // Слой меняется только с тиком: между тиками и пока камера не ушла за запас — прежняя выборка.
        var reach = Fx.Hypot(fx.HalfWidth, fx.HalfHeight) + 2;
        var revision = Tools.Revision().Layers;
        if (_items is null || c.Tick != _itemsTick || revision != _itemsRevision || Fx.Hypot(fx.CameraX - _itemsX, fx.CameraY - _itemsY) + reach > _itemsReach)
        {
            try
            {
                _items = Tools.Items(layer, 0, fx.CameraX, fx.CameraY, reach + Spare);
            }
            catch (Exception)
            {
                // Слой следов не тот, что в data/layers.json (другой набор слоёв), — отметин нет.
                _broken = true;
                return;
            }
            (_itemsTick, _itemsRevision, _itemsX, _itemsY, _itemsReach) = (c.Tick, revision, fx.CameraX, fx.CameraY, reach + Spare);
        }
        var items = _items;
        _wrecks.Clear();
        Tracks.Clear();
        if (_scorch is null)
        {
            _scorch = [.. ScorchSizes.Select(size => fx.Look("decals/scorch/" + size))];
            _pock = fx.Look("decals/pock");
        }
        foreach (var item in items)
        {
            if (item.Fields.Length < 8)
                return;
            var kind = (int)item.Fields[2];
            if (kind == 0)
            {
                Tracks.Add((item.X, item.Y, item.Fields));
                continue;
            }
            if (kind is < Scar or > Wreck)
                continue;
            if (kind == Wreck)
            {
                // Снизу вверх: отметины, затем остовы.
                _wrecks.Add((item.X, item.Y, item.Fields));
                continue;
            }
            var left = Left(item.Fields, kind);
            if (left <= 0 || !OnGround(c, item.X, item.Y))
                continue;
            var variant = (int)(item.Fields[0] % Variants);
            var size = item.Fields[7];
            if (kind == Burn)
                Stamp(item.X, item.Y, ScorchSizes[0], _scorch[0], variant, MarkOpacity * left);
            else if (size < PockSize)
                Stamp(item.X, item.Y, 6, _pock, variant, MarkOpacity * left);
            else
            {
                var pixels = size * ScorchScale * 16;
                var index = 0;
                for (var i = 1; i < ScorchSizes.Length; i++)
                    if (Math.Abs(ScorchSizes[i] - pixels) < Math.Abs(ScorchSizes[index] - pixels))
                        index = i;
                Stamp(item.X, item.Y, ScorchSizes[index], _scorch[index], variant, MarkOpacity * left);
            }
        }
        foreach (var (x, y, fields) in _wrecks)
        {
            var left = Left(fields, Wreck);
            if (left <= 0 || fx.NameOf((int)fields[4]) is not { } type)
                continue;
            var direction = ((int)Math.Round(fields[5] / (Math.PI * 2) * WreckDirections) % WreckDirections + WreckDirections) % WreckDirections;
            if (!_wreckLooks.TryGetValue(type, out var look))
                _wreckLooks[type] = look = fx.Look("decals/wreck/" + type);
            Stamp(x, y, UnitFrame, look, 0, (float)left, direction * Math.PI * 2 / WreckDirections);
        }
    }

    /// <summary>Сколько следа осталось, 0..1: с четверти срока плавно растворяется; срок вышел — 0.</summary>
    private float Left(double[] fields, int kind)
    {
        var age = (fx.C.Tick - fields[1]) * fx.Step;
        var life = Life[kind];
        if (age >= life)
            return 0;
        var share = Math.Max(0, (Math.Max(0, age) / life - FadeFrom) / (1 - FadeFrom));
        return (float)(1 - share * share * (3 - 2 * share));
    }

    private static bool OnGround(ClientContext c, double x, double y) => !c.Terrain(x, y, out var info) || info.Terrain != 0;

    /// <summary>Картинка size × size пикселей с серединой в (x, y), ровно по сетке пикселей мира (stamp).</summary>
    private void Stamp(double x, double y, int size, int look, int frame, float alpha, double angle = 0)
    {
        if (!fx.Visible(x, y, 2))
            return;
        var left = Math.Round((x - size / 32.0) * 16) / 16;
        var top = Math.Round((y - size / 32.0) * 16) / 16;
        fx.Sprite(look, frame, left, top, size / 16.0, size / 16.0, alpha, Fx.Floor, angle: angle);
    }
}
