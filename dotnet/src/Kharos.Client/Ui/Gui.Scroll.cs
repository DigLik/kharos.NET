namespace Kharos.Client.Ui;

public sealed partial class Gui
{
    /// <summary>Ширина русла полосы прокрутки, отступ от края и запас нажатия слева от русла.</summary>
    public const float BarWidth = 6;
    public const float BarMargin = 2;
    public const float BarGrab = 4;
    /// <summary>Наименьшая высота бегунка.</summary>
    public const float BarMinThumb = 16;
    public static readonly Color BarTrackColor = new(0, 0, 0, 90);
    public static readonly Color BarThumbColor = new(139, 168, 196, 170);
    public static readonly Color BarThumbHover = new(176, 204, 232, 215);
    public static readonly Color BarThumbActive = new(214, 230, 245, 255);

    private readonly record struct Bar(Rect Track, Rect Thumb, float Room);

    /// <summary>Сколько прокручиваемого содержимого не влезает в узел (в прошлом кадре) — для держащихся низа.</summary>
    private readonly Dictionary<string, float> _rooms = [];
    private Dictionary<string, Bar> _bars = [], _nextBars = [];
    /// <summary>Где бегунок держат: расстояние от его верха до указателя.</summary>
    private float _grab;

    /// <summary>Прокрутка списка id в пикселях (0 — верх); списка нет — 0.</summary>
    public float ScrollOf(string id) => _scrolls.GetValueOrDefault(id);

    /// <summary>Ставит прокрутку списка id; в пределы она придёт при раскладке.</summary>
    public void ScrollTo(string id, float offset) => _scrolls[id] = offset;

    /// <summary>Номер узла полосы прокрутки списка id: нажатия и наведение на неё приходят под этим номером.</summary>
    public static string BarId(string id) => id + "#bar";

    private void WalkScrollbar(Node node, Rect visible, int hitIndex)
    {
        if (!node.Style.Scroll || node.Style.Id is not { } id || node.Rect.H <= 0) return;
        var room = node.Content - node.Rect.H;
        if (room <= 0.5f) return;
        var rect = node.Rect;
        var track = new Rect(rect.X + rect.W - BarWidth - BarMargin, rect.Y + BarMargin, BarWidth, MathF.Max(rect.H - BarMargin * 2, 1));
        var thumbHeight = Math.Clamp(track.H * rect.H / node.Content, MathF.Min(BarMinThumb, track.H), track.H);
        var offset = Math.Clamp(_scrolls.GetValueOrDefault(id), 0, room);
        var thumb = new Rect(track.X, track.Y + (track.H - thumbHeight) * offset / room, BarWidth, thumbHeight);
        var barId = BarId(id);
        node.HasBar = true;
        (node.BarTrack, node.BarThumb) = (track, thumb);
        node.BarState = (byte)(_press is { Button: 0 } press && press.Id == barId ? 2 : _over == barId ? 1 : 0);
        _nextBars[id] = new Bar(track, thumb, room);
        _nextHits.Add(new Hit(barId, new Rect(track.X - BarGrab, track.Y, track.W + BarGrab, track.H).Intersect(visible), hitIndex));
    }

    /// <summary>Нажали на полосу: хватаем бегунок за место нажатия, а нажатие мимо бегунка ставит его серединой под указатель.</summary>
    private void PressOnBar(string? id, byte button, float py)
    {
        if (button != 0 || id is null || !id.EndsWith("#bar", StringComparison.Ordinal)) return;
        if (!_bars.TryGetValue(id[..^4], out var bar)) return;
        _grab = py >= bar.Thumb.Y && py < bar.Thumb.Y + bar.Thumb.H ? py - bar.Thumb.Y : bar.Thumb.H / 2;
    }

    private void DragScrollbar(in UiInput input)
    {
        if (_press is not { Button: 0, Id: { } id } || !id.EndsWith("#bar", StringComparison.Ordinal) || input.Pointer is not { } pointer) return;
        var list = id[..^4];
        if (!_bars.TryGetValue(list, out var bar)) return;
        var travel = bar.Track.H - bar.Thumb.H;
        if (travel <= 0) return;
        _scrolls[list] = Math.Clamp((pointer.Y - _grab - bar.Track.Y) / travel, 0, 1) * bar.Room;
    }
}
