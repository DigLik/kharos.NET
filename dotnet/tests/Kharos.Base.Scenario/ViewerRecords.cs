using Kharos.Client.Game;
using Kharos.Client.Ui;


namespace Kharos.Base.Scenario;

/// <summary>Звуковая команда мода: запись (Ops.Play, SoundAt, Loop, Voice), имя, номер голоса, числа, кадр.</summary>
/// <param name="Op">Запись выхода кадра.</param>
/// <param name="Name">Имя звука; у голоса — вид (null — голос по графу мода).</param>
/// <param name="Voice">Номер голоса мода (у Voice), иначе 0.</param>
/// <param name="Values">Числа записи: Play — громкость, панорама, скорость; SoundAt — x, y, громкость, слышимость,
/// расстройка; Loop — громкость, панорама; Voice — шина, начало, длина, нота, сила.</param>
/// <param name="Frame">Номер кадра зрителя (с 1).</param>
public readonly record struct SoundCall(byte Op, string? Name, uint Voice, float[] Values, int Frame)
{
    public string Kind => Op switch
    {
        Ops.Play => "Play",
        Ops.SoundAt => "SoundAt",
        Ops.Loop => "Loop",
        Ops.Voice => "Voice",
        _ => $"0x{Op:x2}",
    };

    public override string ToString() => $"{Kind}({Name}) @{Frame}";
}

/// <summary>Звуковые команды модов со всех кадров зрителя (устройства звука нет: команды только пишутся).</summary>
public sealed class ViewerSounds
{
    private readonly List<SoundCall> _calls = [];

    /// <summary>Все команды по порядку.</summary>
    public IReadOnlyList<SoundCall> All => _calls;

    /// <summary>Имена по порядку (у голосов — вид, по графу — «voice»).</summary>
    public IReadOnlyList<string> Names => [.. _calls.Select(call => call.Name ?? "voice")];

    /// <summary>Команды одной записи (Ops.Play, SoundAt, Loop, Voice).</summary>
    public IReadOnlyList<SoundCall> Of(byte op) => [.. _calls.Where(call => call.Op == op)];

    /// <summary>Сколько раз звучало имя (любой записью).</summary>
    public int Count(string name) => _calls.Count(call => call.Name == name);

    /// <summary>Звучало ли имя.</summary>
    public bool Played(string name) => Count(name) > 0;

    /// <summary>Забыть записанное.</summary>
    public void Clear() => _calls.Clear();

    internal void Take(List<FrameRecord> records, int frame)
    {
        foreach (var record in records)
            if (record.Op is Ops.Play or Ops.SoundAt or Ops.Loop or Ops.Voice)
                _calls.Add(new SoundCall(record.Op, record.Name, record.Id, record.Values, frame));
    }
}

/// <summary>Линия, круг или рамка мода (0x20–0x22): точки в тайлах, цвет 0xAARRGGBB/0xRRGGBB, толщина в пикселях (NaN — заливка).</summary>
public readonly record struct DrawCall(byte Op, uint Color, float[] Values);

/// <summary>
/// Рисунки модов за кадр (записи 0x20–0x26): линии, круги и рамки — по мере разбора, свободные спрайты, Paint, огни и
/// лучи — из <see cref="ClientScene"/> этого кадра.
/// </summary>
public sealed class ViewerDraws
{
    public IReadOnlyList<DrawCall> Lines { get; private init; } = [];
    public IReadOnlyList<DrawCall> Circles { get; private init; } = [];
    public IReadOnlyList<DrawCall> Rects { get; private init; } = [];
    public IReadOnlyList<SceneSprite> Sprites { get; private init; } = [];
    public IReadOnlyDictionary<uint, ScenePaint> Paints { get; private init; } = new Dictionary<uint, ScenePaint>();
    public IReadOnlyList<SceneLight> Lights { get; private init; } = [];
    public IReadOnlyList<SceneBeam> Beams { get; private init; } = [];

    /// <summary>Сколько записей этого вида (Ops.Line … Ops.Beam) в кадре.</summary>
    public int Count(byte op) => op switch
    {
        Ops.Line => Lines.Count,
        Ops.Circle => Circles.Count,
        Ops.Rect => Rects.Count,
        Ops.Sprite => Sprites.Count,
        Ops.Paint => Paints.Count,
        Ops.Light => Lights.Count,
        Ops.Beam => Beams.Count,
        _ => 0,
    };

    /// <summary>Всего записей 0x20–0x26.</summary>
    public int Total => Lines.Count + Circles.Count + Rects.Count + Sprites.Count + Paints.Count + Lights.Count + Beams.Count;

    internal static ViewerDraws Of(List<FrameRecord> records, ClientScene scene)
    {
        List<DrawCall> Pick(byte op) => [.. records.Where(record => record.Op == op).Select(record => new DrawCall(op, record.Id, record.Values))];
        return new ViewerDraws
        {
            Lines = Pick(Ops.Line),
            Circles = Pick(Ops.Circle),
            Rects = Pick(Ops.Rect),
            Sprites = [.. scene.Sprites],
            Paints = new Dictionary<uint, ScenePaint>(scene.Paints),
            Lights = [.. scene.Lights],
            Beams = [.. scene.Beams],
        };
    }
}

/// <summary>Дерево интерфейса последнего кадра: узлы по id, тексты, кнопки.</summary>
public sealed class ViewerUi(Gui gui)
{
    /// <summary>Корни дерева (панели и подсказки).</summary>
    public IReadOnlyList<Node> Roots => gui.Roots;

    /// <summary>Все тексты по порядку обхода.</summary>
    public List<string> Texts() => [.. Walk().Where(node => node.Kind == LeafKind.Text).Select(node => node.Text)];

    /// <summary>Все id узлов по порядку обхода.</summary>
    public List<string> Ids() => [.. Walk().Select(node => node.Style.Id).OfType<string>().Where(id => id.Length > 0)];

    /// <summary>Узел по id; нет — null.</summary>
    public Node? Find(string id) => Walk().FirstOrDefault(node => node.Style.Id == id);

    /// <summary>Есть ли узел.</summary>
    public bool Has(string id) => Find(id) is not null;

    /// <summary>Тексты внутри узла id, через пробел; нет узла — null.</summary>
    public string? Text(string id) => Find(id) is { } node ? string.Join(" ", Walk(node).Where(n => n.Kind == LeafKind.Text).Select(n => n.Text)) : null;

    /// <summary>Кнопки кадра (узлы с рамкой кнопки): id.</summary>
    public List<string> Buttons() => [.. Walk().Where(IsButton).Select(node => node.Style.Id!)];

    /// <summary>Кнопка id есть и нажимается.</summary>
    public bool Enabled(string id) => Find(id) is { } node && IsButton(node) && node.Style.Skin != Skin.ButtonDisabled;

    /// <summary>Место узла (по раскладке прошлого кадра); нет — null.</summary>
    public Rect? RectOf(string id) => gui.RectOf(id);

    /// <summary>Середина узла; нет — null.</summary>
    public (float X, float Y)? Center(string id) => RectOf(id) is { } rect ? (rect.X + rect.W / 2, rect.Y + rect.H / 2) : null;

    private static bool IsButton(Node node) =>
        node.Style.Id is { Length: > 0 } && node.Style.Skin is Skin.Button or Skin.ButtonHover or Skin.ButtonActive or Skin.ButtonDisabled or Skin.ButtonPrimary or Skin.ButtonPrimaryHover;

    private IEnumerable<Node> Walk()
    {
        foreach (var root in gui.Roots)
            foreach (var node in Walk(root))
                yield return node;
    }

    private static IEnumerable<Node> Walk(Node node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var inner in Walk(child))
                yield return inner;
    }
}

/// <summary>Номера записей выхода кадра (ClientAbi, «Выход кадра»), что пишет зритель.</summary>
public static class Ops
{
    public const byte Line = 0x20, Circle = 0x21, Rect = 0x22, Sprite = 0x23, Paint = 0x24, Light = 0x25, Beam = 0x26;
    public const byte Play = 0x40, SoundAt = 0x41, Loop = 0x42, Voice = 0x43;
}
