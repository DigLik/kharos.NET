using System.Diagnostics;
using Kharos.Client.Game;
using Kharos.Engine.Terrain;

namespace Kharos.Client.Shell;

/// <summary>
/// Схема будущей карты для окна новой игры (preview.ts): как мини-карта в игре, только без гор — они мельче пикселя
/// схемы (<see cref="TerrainGenerator.Sample"/>). Считается порциями, по <see cref="Budget"/> за кадр: карта
/// проявляется сверху вниз, а меню не подвисает. Пока настройки меняют, заново не считается — только после паузы
/// <see cref="Settle"/>. Картинка — RGBA <see cref="Side"/>², её показывает меню (растр мини-карты рендера).
/// </summary>
public sealed class TerrainPreview
{
    /// <summary>Сторона предпросмотра в пикселях.</summary>
    public const int Side = 256;

    /// <summary>Цвет вне карты, RGBA.</summary>
    private static readonly byte[] Outside = [8, 17, 28, 255];

    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(8);
    public const double Settle = 0.12;

    private string _key = "";
    private GeneratorConfig? _config;
    private int _width, _height, _row;
    private double _changed = double.NegativeInfinity;
    private readonly int[] _counts = new int[4];
    private int _inside;
    private bool _started;

    /// <summary>Пиксели схемы, RGBA.</summary>
    public byte[] Pixels { get; } = new byte[Side * Side * 4];

    /// <summary>Растёт, когда пиксели поменялись: рендеру — загрузить заново.</summary>
    public int Revision { get; private set; }

    /// <summary>Карта досчитана: схема яркая.</summary>
    public bool Ready { get; private set; }

    /// <summary>Доли карты под скалами (на них строят и на них руда) и под болотами; null — не досчитано.</summary>
    public (double Rock, double Swamp)? Shares { get; private set; }

    /// <summary>Показать карту с такой местностью и размером: другая — считается заново после паузы.</summary>
    public void Show(GeneratorConfig config, int width, int height, double now)
    {
        var key = $"{config}|{width}|{height}";
        if (key == _key)
            return;
        (_key, _config, _width, _height, _changed) = (key, config, width, height, now);
        (Ready, Shares, _started) = (false, null, false);
    }

    /// <summary>Досчитать порцию: в кадр меню, не дольше budget (null — <see cref="Budget"/>, ноль — до конца).</summary>
    public void Step(double now, TimeSpan? budget = null)
    {
        if (_config is not { } config || Ready || now - _changed < Settle)
            return;
        if (!_started)
        {
            (_row, _inside, _started) = (0, 0, true);
            Array.Clear(_counts);
        }
        var limit = budget ?? Budget;
        var clock = Stopwatch.StartNew();
        // Карта вписана в квадрат по большей стороне; что вне её — тёмное поле.
        var scale = Math.Max(_width, _height) / (double)Side;
        while (_row < Side && (limit == TimeSpan.Zero || clock.Elapsed < limit))
        {
            var y = (_row + 0.5 - Side / 2.0) * scale;
            for (var column = 0; column < Side; column++)
            {
                var x = (column + 0.5 - Side / 2.0) * scale;
                var at = (_row * Side + column) * 4;
                if (Math.Abs(x) > _width / 2.0 || Math.Abs(y) > _height / 2.0)
                {
                    Outside.CopyTo(Pixels, at);
                    continue;
                }
                var (terrain, biome) = TerrainGenerator.Sample(config, x, y);
                _counts[(int)terrain & 3]++;
                _inside++;
                var color = ClientGame.DefaultMinimap[((int)biome & 3) * 4 + ((int)terrain & 3)];
                (Pixels[at], Pixels[at + 1], Pixels[at + 2], Pixels[at + 3]) = ((byte)(color >> 16), (byte)(color >> 8), (byte)color, 255);
            }
            _row++;
        }
        Revision++;
        if (_row < Side)
            return;
        var total = Math.Max(1, _inside);
        Shares = (_counts[(int)TerrainType.Rock] / (double)total, _counts[(int)TerrainType.Swamp] / (double)total);
        Ready = true;
    }
}
