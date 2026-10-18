using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk.Client;

/// <summary>Кнопки указателя.</summary>
[Flags]
public enum PointerButton : byte { None = 0, Left = ClientAbi.ButtonLeft, Right = ClientAbi.ButtonRight, Middle = ClientAbi.ButtonMiddle }

/// <summary>
/// Ввод кадра (c.input пилота): указатель на экране и в мире, кнопки, колесо, клавиши. Мир — по камере после
/// обработчиков камеры этого кадра.
/// </summary>
public sealed class Input
{
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal), _hits = new(StringComparer.Ordinal);
    private byte _down, _pressed, _released;

    /// <summary>Указатель на экране в логических пикселях; вне окна — NaN.</summary>
    public float X { get; private set; }
    public float Y { get; private set; }

    /// <summary>Указатель в мире, тайлы.</summary>
    public double WorldX { get; internal set; }
    public double WorldY { get; internal set; }

    /// <summary>Указатель в окне.</summary>
    public bool Inside { get; private set; }
    public bool Focused { get; private set; }
    /// <summary>Последним указателем было касание.</summary>
    public bool Touch { get; private set; }
    /// <summary>Указатель над интерфейсом (по местам прошлого кадра): миру щелчок не достаётся.</summary>
    public bool OverUi { get; private set; }
    public bool Shift { get; private set; }
    public bool Ctrl { get; private set; }
    public bool Alt { get; private set; }
    /// <summary>Колесо за кадр: строк, вверх — больше нуля.</summary>
    public float Wheel { get; private set; }
    /// <summary>Набранный за кадр текст.</summary>
    public string Text { get; private set; } = "";

    /// <summary>Зажатые клавиши: "a", "1", "shift", "left", "escape"…</summary>
    public IReadOnlySet<string> Keys => _keys;

    /// <summary>Нажатые за кадр клавиши.</summary>
    public IReadOnlySet<string> Hits => _hits;

    public bool Down(PointerButton button) => (_down & (byte)button) != 0;
    public bool Pressed(PointerButton button) => (_pressed & (byte)button) != 0;
    public bool Released(PointerButton button) => (_released & (byte)button) != 0;

    internal void Read(ref ByteReader r)
    {
        var flags = r.U8();
        Inside = (flags & ClientAbi.InputInside) != 0;
        Focused = (flags & ClientAbi.InputFocused) != 0;
        Touch = (flags & ClientAbi.InputTouch) != 0;
        OverUi = (flags & ClientAbi.InputOverUi) != 0;
        Shift = (flags & ClientAbi.InputShift) != 0;
        Ctrl = (flags & ClientAbi.InputCtrl) != 0;
        Alt = (flags & ClientAbi.InputAlt) != 0;
        X = r.F32();
        Y = r.F32();
        _down = r.U8();
        _pressed = r.U8();
        _released = r.U8();
        Wheel = r.F32();
        _keys.Clear();
        for (int i = 0, n = r.U16(); i < n; i++) _keys.Add(r.Str());
        _hits.Clear();
        for (int i = 0, n = r.U16(); i < n; i++) _hits.Add(r.Str());
        Text = r.Str();
    }
}

/// <summary>Камера: центр в тайлах и масштаб (пикселей на тайл). Её двигают обработчики камеры.</summary>
public sealed class Camera
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Zoom { get; set; } = 32;
}

/// <summary>Что пишет атмосфера: свет 0..1, ветер в тайлах в секунду, осадки 0..1.</summary>
public sealed class Atmosphere
{
    public float Light { get; set; } = 1;
    public float WindX { get; set; }
    public float WindY { get; set; }
    public float Precipitation { get; set; }
}

/// <summary>
/// Рисование поверх мира (c.draw): в тайлах, толщина — в пикселях экрана. Цвет — 0xRRGGBB или 0xAARRGGBB,
/// alpha — множитель.
/// </summary>
public sealed class Draw
{
    private readonly ByteWriter _out;

    internal Draw(ByteWriter output) => _out = output;

    /// <summary>Цвет мода в rgba (как color в script.rs).</summary>
    public static uint Rgba(uint value, float alpha = 1)
    {
        var a = (value > 0xffffff ? (value >> 24) / 255f : 1f) * alpha;
        return (value >> 16 & 0xff) | (value >> 8 & 0xff) << 8 | (value & 0xff) << 16 | (uint)Ui.Byte(Math.Clamp(a, 0, 1) * 255) << 24;
    }

    public void Line(double x1, double y1, double x2, double y2, uint color, float width = 1, float alpha = 1) =>
        _out.U8(ClientAbi.OpLine).F32((float)x1).F32((float)y1).F32((float)x2).F32((float)y2).U32(Rgba(color, alpha)).F32(width);

    /// <summary>Круг: width — толщина кольца в пикселях, null — заливка.</summary>
    public void Circle(double x, double y, double radius, uint color, float? width = null, float alpha = 1) =>
        _out.U8(ClientAbi.OpCircle).F32((float)x).F32((float)y).F32((float)radius).U32(Rgba(color, alpha)).F32(width ?? float.NaN);

    /// <summary>Прямоугольник по углам: width — толщина рамки в пикселях, null — заливка.</summary>
    public void Rect(double x1, double y1, double x2, double y2, uint color, float? width = null, float alpha = 1) =>
        _out.U8(ClientAbi.OpRect).F32((float)x1).F32((float)y1).F32((float)x2).F32((float)y2).U32(Rgba(color, alpha)).F32(width ?? float.NaN);
}

/// <summary>
/// Что видит клиентский мод за кадр — <c>c</c> пилота (docs/mods.md, «c»): ввод, камера, экран, время, игрок,
/// интерфейс, рисование, звук, мир на чтение (Position и Facing — сглаженные между тиками) и действия: приказ хосту,
/// звук в мире, частицы, следы на земле, атмосфера.
/// </summary>
public sealed class ClientContext
{
    /// <summary>Близко: при таком масштабе звук полный, при вдвое меньшем — вдвое тише (hearing.ts).</summary>
    public const double NearZoom = 32;

    private readonly ByteWriter _out = new();
    private readonly List<HostSignal> _signals = [];
    private ComponentRef? _proto, _owner;

    /// <summary>Свой кадр вне wasm (тесты): выход пишется в собственный буфер.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ClientContext()
    {
        Ui = new Ui(_out);
        Draw = new Draw(_out);
        Audio = new ClientAudio(_out);
    }

    public Input Input { get; } = new();
    public Camera Camera { get; } = new();
    public Atmosphere Atmosphere { get; } = new();
    public Ui Ui { get; }
    public Draw Draw { get; }
    public ClientAudio Audio { get; }

    /// <summary>Общая память обработчиков мода (selection и т. п.).</summary>
    public Dictionary<string, object?> Shared { get; } = new(StringComparer.Ordinal);

    /// <summary>Секунды с запуска клиента и длина кадра.</summary>
    public double Time { get; private set; }
    public double Dt { get; private set; }

    /// <summary>Тик реплики и доля тика с его прихода (0..1).</summary>
    public ulong Tick { get; private set; }
    public double Alpha { get; private set; }

    /// <summary>Секунды игры, сглаженные между тиками: по ним сутки и погода — у всех одинаково.</summary>
    public double Clock { get; private set; }

    /// <summary>Длина тика хоста, секунды.</summary>
    public double TickDt { get; private set; }

    /// <summary>Свой игрок.</summary>
    public uint Player { get; private set; }

    /// <summary>Экран в логических пикселях и физических пикселей на логический.</summary>
    public float ScreenWidth { get; private set; }
    public float ScreenHeight { get; private set; }
    public float Scale { get; private set; }

    /// <summary>Сигналы хоста этого кадра.</summary>
    public IReadOnlyList<HostSignal> Signals => _signals;

    /// <summary>Идущая партия (data/games.json).</summary>
    public string Game { get; private set; } = "";

    internal WorldSchema? Schema { get; set; }

    /// <summary>Компонент схемы мира по имени.</summary>
    public ComponentRef Component(string name) => (Schema ?? throw new InvalidOperationException("схемы ещё нет")).Get(name);

    // ------------------------------------------------------------------ мир на чтение

    /// <summary>Сущности со всеми компонентами — по возрастанию номеров.</summary>
    public uint[] Each(params ReadOnlySpan<ComponentRef> components)
    {
        ClientTools.Begin();
        ClientTools.U32((uint)components.Length);
        foreach (var component in components) ClientTools.U32((uint)component.Id);
        return Entities(ClientTools.Call(ClientAbi.ToolEach));
    }

    /// <summary>Значения компонента; Position и Facing — сглаженные. Нет — null.</summary>
    public ComponentValues? Get(uint entity, ComponentRef component) => Tools.Get(entity, component);

    public bool Has(uint entity, ComponentRef component) => Tools.Has(entity, component);

    public bool Alive(uint entity) => Tools.Alive(entity);

    /// <summary>Вид сущности (номер); нет — -1.</summary>
    public int Proto(uint entity)
    {
        _proto ??= Component("Proto");
        return Tools.Get(entity, _proto) is { } values ? (int)values.Number(_proto["id"]) : -1;
    }

    /// <summary>Номер вида «раздел/имя»; нет — -1.</summary>
    public int ProtoId(string path) => Tools.ProtoId(path);

    /// <summary>«Раздел/имя» вида по номеру.</summary>
    public string? ProtoPath(int id) => Tools.ProtoName(id);

    /// <summary>Своя ли сущность.</summary>
    public bool Mine(uint entity)
    {
        _owner ??= Component("Owner");
        return Tools.Get(entity, _owner) is { } values && values.Number(_owner["player"]) == Player;
    }

    /// <summary>Сущность под точкой мира: юнит по радиусу, здание по прямоугольнику; ближайшая.</summary>
    public uint? Pick(double x, double y)
    {
        ClientTools.Begin();
        ClientTools.F64(x);
        ClientTools.F64(y);
        var entity = BinaryPrimitives.ReadUInt32LittleEndian(ClientTools.Call(ClientAbi.ToolPick));
        return entity == 0 ? null : entity;
    }

    /// <summary>Сущности с Position в прямоугольнике мира и со всеми компонентами.</summary>
    public uint[] Within(double x1, double y1, double x2, double y2, params ReadOnlySpan<ComponentRef> components)
    {
        ClientTools.Begin();
        ClientTools.F64(x1);
        ClientTools.F64(y1);
        ClientTools.F64(x2);
        ClientTools.F64(y2);
        ClientTools.U32((uint)components.Length);
        foreach (var component in components) ClientTools.U32((uint)component.Id);
        return Entities(ClientTools.Call(ClientAbi.ToolWithin));
    }

    /// <summary>Видит ли свой игрок тайл сейчас (тумана нет — всё видно).</summary>
    public bool Sees(double x, double y) => Fog(ClientAbi.ToolSees, x, y);

    /// <summary>Разведан ли тайл.</summary>
    public bool Explored(double x, double y) => Fog(ClientAbi.ToolExplored, x, y);

    /// <summary>Место и направление сущности сейчас — между прошлым и этим тиком; нет — null.</summary>
    public (double X, double Y, double Angle)? Pose(uint entity)
    {
        Span<(double, double, double)> one = stackalloc (double, double, double)[1];
        Poses([entity], one);
        return double.IsNaN(one[0].Item1) ? null : one[0];
    }

    /// <summary>Места и направления пакетом; нет сущности — NaN.</summary>
    public void Poses(ReadOnlySpan<uint> entities, Span<(double X, double Y, double Angle)> into)
    {
        ClientTools.Begin();
        ClientTools.U32((uint)entities.Length);
        foreach (var entity in entities) ClientTools.U32(entity);
        MemoryMarshal.Cast<byte, (double, double, double)>(ClientTools.Call(ClientAbi.ToolPose)[..(entities.Length * 24)]).CopyTo(into);
    }

    /// <summary>Поле-столбец компонента у сущностей пакетом; нет компонента — NaN.</summary>
    public void Column(ReadOnlySpan<uint> entities, ComponentRef component, string field, Span<double> into)
    {
        ClientTools.Begin();
        ClientTools.U32((uint)component.Id);
        ClientTools.U32((uint)component[field].Index);
        ClientTools.U32((uint)entities.Length);
        foreach (var entity in entities) ClientTools.U32(entity);
        MemoryMarshal.Cast<byte, double>(ClientTools.Call(ClientAbi.ToolColumn)[..(entities.Length * 8)]).CopyTo(into);
    }

    /// <summary>Местность в точке; false — у партии нет местности.</summary>
    public bool Terrain(double x, double y, out TerrainInfo info) => Tools.Terrain(x, y, out info);

    // ------------------------------------------------------------------ экран и слух

    public (double X, double Y) ToScreen(double x, double y) => ((x - Camera.X) * Camera.Zoom + ScreenWidth / 2.0, (y - Camera.Y) * Camera.Zoom + ScreenHeight / 2.0);

    public (double X, double Y) ToWorld(double x, double y) => ((x - ScreenWidth / 2.0) / Camera.Zoom + Camera.X, (y - ScreenHeight / 2.0) / Camera.Zoom + Camera.Y);

    /// <summary>Насколько слышно то, что в (x, y): 1 — на экране вблизи, 0 — далеко за краем (hearing.ts).</summary>
    public float Nearness(double x, double y, double reach = 0.6)
    {
        var halfWidth = ScreenWidth / 2.0 / Camera.Zoom;
        var halfHeight = ScreenHeight / 2.0 / Camera.Zoom;
        var outside = Math.Max(0, Math.Max(Math.Abs(x - Camera.X) - halfWidth, Math.Abs(y - Camera.Y) - halfHeight));
        return (float)(Math.Max(0, 1 - outside / (halfWidth * reach)) * Math.Min(1, Camera.Zoom / NearZoom));
    }

    /// <summary>Сторона x: −1 — у левого края экрана, 1 — у правого.</summary>
    public float Side(double x) => (float)((x - Camera.X) / (ScreenWidth / 2.0 / Camera.Zoom));

    /// <summary>Случайное 0..1 от координат и зерна — тот же hash, что у генератора местности.</summary>
    public static double Hash(double x, double y, double seed)
    {
        unchecked
        {
            var h = Int32(x) * 0x27d4eb2d ^ Int32(y) * 0x165667b1 ^ Int32(seed);
            h = (h ^ (int)((uint)h >> 15)) * (int)0x85ebca6b;
            h = (h ^ (int)((uint)h >> 13)) * (int)0xc2b2ae35;
            return (uint)(h ^ (int)((uint)h >> 16)) / 4294967296.0;
        }
    }

    /// <summary>ToInt32 из JS: отбросить дробь, по модулю 2³², NaN и ∞ — 0.</summary>
    private static int Int32(double x)
    {
        if (x > -2147483649.0 && x < 2147483648.0) return (int)x;
        if (!double.IsFinite(x)) return 0;
        var modulo = Math.Truncate(x) % 4294967296.0;
        if (modulo < 0) modulo += 4294967296.0;
        return unchecked((int)(uint)modulo);
    }

    // ------------------------------------------------------------------ действия

    /// <summary>
    /// Приказ хосту (выполнится в начале его тика); payload — формат знает обработчик приказа, обычно
    /// <c>MemoryPackSerializer.Serialize(приказ)</c> в коде мода (так NativeAOT видит конкретный тип).
    /// </summary>
    public void Send(string order, ReadOnlySpan<byte> payload) => _out.U8(ClientAbi.OpSend).Str(order).Blob(payload);

    /// <summary>Разовый звук в мире (c:sound): тише вдали и в своей стороне, высота чуть случайна.</summary>
    public void Sound(string name, double x, double y, float volume = 1, float reach = 0.6f, float detune = 0.08f) =>
        _out.U8(ClientAbi.OpSoundAt).Str(name).F64(x).F64(y).F32(volume).F32(reach).F32(detune);

    /// <summary>Разовый звук без места: громкость и сторона как есть.</summary>
    public void Sound(string name, float volume = 1, float pan = 0) => Audio.Play(name, volume, pan);

    /// <summary>Частица: место и скорость в тайлах, сколько живёт, цвет, размер в текселях, сносит ли ветром.</summary>
    public void Particle(double x, double y, double vx = 0, double vy = 0, float life = 1, uint color = 0xffffff, float size = 1, bool drift = true) =>
        _out.U8(ClientAbi.OpParticle).F32((float)x).F32((float)y).F32((float)vx).F32((float)vy).F32(life).U32(color).F32(size).U8(drift ? (byte)1 : (byte)0);

    /// <summary>
    /// След на земле (decalsPass.ts): отрезок и ширина в тайлах, цвет 0xRRGGBB; под юнитами, ночью темнеет с землёй;
    /// density — доля закрашенных пикселей; с четверти life растворяется; вне обзора забывается.
    /// </summary>
    public void Decal(double fromX, double fromY, double toX, double toY, float width = 1 / 16f, uint color = 0xffffff, float opacity = 1, float density = 1, float life = 60) =>
        Decal(fromX, fromY, toX, toY, width, ((color >> 16 & 0xff) / 255f, (color >> 8 & 0xff) / 255f, (color & 0xff) / 255f), opacity, density, life);

    /// <summary>След на земле с цветом r, g, b от 0 до 1.</summary>
    public void Decal(double fromX, double fromY, double toX, double toY, float width, (float R, float G, float B) color, float opacity = 1, float density = 1, float life = 60)
    {
        if (opacity <= 0 || life <= 0) return;
        _out.U8(ClientAbi.OpDecal).F32((float)fromX).F32((float)fromY).F32((float)toX).F32((float)toY).F32(width)
            .F32(color.R).F32(color.G).F32(color.B).F32(opacity).F32(density).F32(life);
    }

    /// <summary>Палитра сторон: своя, чужая, союзная, ничья — по три цвета 0xRRGGBB (тёмный, основной, светлый).</summary>
    public void Palette(ReadOnlySpan<uint> colors)
    {
        if (colors.Length != 12)
            throw new ArgumentException("палитра — 12 цветов", nameof(colors));
        _out.U8(ClientAbi.OpPalette);
        foreach (var color in colors) _out.U32(color);
    }

    // ------------------------------------------------------------------ кадр

    /// <summary>
    /// Кадр вне wasm (тесты, сверка): разбор входа, body, выход. В моде то же делает kh_frame по обработчикам
    /// <see cref="ClientBuilder"/>.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ReadOnlySpan<byte> Frame(ReadOnlySpan<byte> input, Action<ClientContext> body)
    {
        Begin(input);
        body(this);
        End();
        return _out.Written;
    }

    /// <summary>Кадр по обработчикам мода: сигналы → камера → … → музыка; сбой обработчика — в журнал, кадр идёт дальше.</summary>
    internal unsafe (nint Pointer, int Length) Frame(ReadOnlySpan<byte> input, ClientBuilder handlers, Action<string> fail)
    {
        Begin(input);
        foreach (var signal in _signals)
            if (handlers.Signals.TryGetValue(signal.Name, out var list))
                foreach (var handler in list)
                    Guard($"сигнал {signal.Name}", () => handler(this, signal), fail);
        for (var stage = 0; stage < handlers.Stages.Length; stage++)
        {
            foreach (var (name, handler) in handlers.Stages[stage])
                Guard(name, () => handler(this), fail);
            // После камеры ввод в мире — по новой камере; клиенту — камера до рисунков поверх мира (толщина в пикселях).
            if (stage == (int)ClientBuilder.Stage.Camera)
            {
                (Input.WorldX, Input.WorldY) = ToWorld(Input.X, Input.Y);
                WriteCamera();
            }
        }
        End();
        return ((nint)_out.Pointer, _out.Length);
    }

    private void Guard(string name, Action run, Action<string> fail)
    {
        var depth = Ui.Depth;
        try
        {
            run();
        }
        catch (Exception error)
        {
            Ui.Unwind(depth);
            fail($"{name}: {error.GetType().Name}: {error.Message}");
        }
    }

    internal void Load(LoadContext ctx, WorldSchema schema)
    {
        Schema = schema;
        Game = ctx.Game;
    }

    private void Begin(ReadOnlySpan<byte> input)
    {
        _out.Clear();
        var r = new ByteReader(input);
        Time = r.F64();
        Dt = r.F64();
        Tick = r.U64();
        Alpha = r.F64();
        Clock = r.F64();
        TickDt = r.F64();
        Player = r.U32();
        ScreenWidth = r.F32();
        ScreenHeight = r.F32();
        Scale = r.F32();
        Camera.X = r.F64();
        Camera.Y = r.F64();
        Camera.Zoom = r.F64();
        Input.Read(ref r);
        (Input.WorldX, Input.WorldY) = ToWorld(Input.X, Input.Y);
        Ui.Begin(ref r);
        Audio.Begin(ref r);
        _signals.Clear();
        for (int i = 0, n = r.U16(); i < n; i++)
        {
            var tick = r.U64();
            var name = r.Str();
            _signals.Add(new HostSignal(tick, name, r.Blob().ToArray()));
        }
        Atmosphere.Light = r.F32();
        Atmosphere.WindX = r.F32();
        Atmosphere.WindY = r.F32();
        Atmosphere.Precipitation = r.F32();
    }

    private void End()
    {
        Ui.Unwind(0);
        WriteCamera();
        _out.U8(ClientAbi.OpAtmosphere).U8(ClientAbi.AtmosphereLight | ClientAbi.AtmosphereWind | ClientAbi.AtmospherePrecipitation)
            .F32(Atmosphere.Light).F32(Atmosphere.WindX).F32(Atmosphere.WindY).F32(Atmosphere.Precipitation);
    }

    private void WriteCamera() => _out.U8(ClientAbi.OpCamera).F64(Camera.X).F64(Camera.Y).F64(Camera.Zoom);

    private static uint[] Entities(ReadOnlySpan<byte> output)
    {
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(output);
        return MemoryMarshal.Cast<byte, uint>(output.Slice(4, count * 4)).ToArray();
    }

    private static bool Fog(int tool, double x, double y)
    {
        ClientTools.Begin();
        ClientTools.U32(1);
        ClientTools.F64(x);
        ClientTools.F64(y);
        return ClientTools.Call(tool)[0] != 0;
    }
}
