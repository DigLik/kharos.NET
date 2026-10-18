using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Kharos.Engine.Terrain;
using Kharos.Engine.World;
using Kharos.Mod.Abi;

namespace Kharos.Host;

/// <summary>
/// Инструменты движка для модов (kh.tool, форматы — ModAbi.Tool*): пакетный поиск в круге, расталкивание, местность,
/// чтение компонентов, виды, строки, память мода. Только чтение мира — каким он был до идущей системы.
/// </summary>
public sealed class EngineTools(Host host)
{
    private byte[] _result = new byte[4096];
    private int _length;
    private readonly List<uint> _near = [];
    private double[] _x = [], _y = [], _radius = [], _moving = [];

    /// <summary>Вызов инструмента модом mod (null — без мода: тесты). Ответ — длина; места мало — ничего не записано.</summary>
    public int Call(HostMod? mod, int id, ReadOnlySpan<byte> input, Span<byte> output)
    {
        _length = 0;
        try
        {
            var code = Run(mod, id, input);
            if (code < 0)
                return code;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IndexOutOfRangeException or KeyNotFoundException or FormatException or OverflowException)
        {
            host.Report(mod, $"инструмент {id}", error.Message, null);
            return ModAbi.ErrorBadInput;
        }
        if (_length <= output.Length)
            _result.AsSpan(0, _length).CopyTo(output);
        return _length;
    }

    /// <summary>Ответ последнего вызова (для тестов и вызовов из C#).</summary>
    public ReadOnlySpan<byte> Result => _result.AsSpan(0, _length);

    private int Run(HostMod? mod, int id, ReadOnlySpan<byte> input)
    {
        var world = host.World;
        var r = new Input(input);
        switch (id)
        {
            case ModAbi.ToolNear:
            {
                var m = r.Count(24);
                var k = r.Count(4);
                Span<int> components = k <= 64 ? stackalloc int[k] : new int[k];
                for (var at = 0; at < k; at++)
                    components[at] = Component(r.U32());
                for (var at = 0; at < m; at++)
                {
                    var (x, y, radius) = (r.F64(), r.F64(), r.F64());
                    _near.Clear();
                    world.Near(x, y, radius, components, _near);
                    U32((uint)_near.Count);
                    foreach (var entity in _near)
                        U32(entity);
                }
                break;
            }
            case ModAbi.ToolSeparate:
            {
                var n = r.Count(24);
                var strength = r.F64();
                var flags = r.U32();
                Grow(ref _x, n);
                Grow(ref _y, n);
                Grow(ref _radius, n);
                r.Doubles(_x.AsSpan(0, n));
                r.Doubles(_y.AsSpan(0, n));
                r.Doubles(_radius.AsSpan(0, n));
                var moving = Span<double>.Empty;
                if ((flags & 1) != 0)
                {
                    Grow(ref _moving, n);
                    moving = _moving.AsSpan(0, n);
                    r.Doubles(moving);
                }
                Separate(_x.AsSpan(0, n), _y.AsSpan(0, n), _radius.AsSpan(0, n), moving, strength);
                Doubles(_x.AsSpan(0, n));
                Doubles(_y.AsSpan(0, n));
                break;
            }
            case ModAbi.ToolTerrain:
            {
                var n = r.Count(16);
                if (host.Land is not { } land)
                    break;
                for (var at = 0; at < n; at++)
                {
                    var (x, y) = (r.F64(), r.F64());
                    var (terrain, biome, foot) = Terrain(land, x, y);
                    Room(3);
                    _result[_length - 3] = terrain;
                    _result[_length - 2] = biome;
                    _result[_length - 1] = foot ? (byte)1 : (byte)0;
                }
                break;
            }
            case ModAbi.ToolGet:
            {
                var entity = r.U32();
                var component = Component(r.U32());
                var store = world.Store(component);
                var row = store.RowOf(entity);
                if (row < 0)
                    break;
                foreach (var field in store.Def.Fields)
                {
                    if (field.Columnar)
                    {
                        BinaryPrimitives.WriteDoubleLittleEndian(Room(8), store.Column(field.Slot)[row]);
                        continue;
                    }
                    var bytes = store.BytesAt(field.Slot, row);
                    U32((uint)bytes.Length);
                    bytes.CopyTo(Room(bytes.Length));
                }
                break;
            }
            case ModAbi.ToolHas:
            {
                var raw = (int)r.U32();
                var component = raw == -1 ? -1 : Component((uint)raw);
                var n = r.Count(4);
                var into = Room(n);
                for (var at = 0; at < n; at++)
                {
                    var entity = r.U32();
                    into[at] = (component < 0 ? world.Alive(entity) : world.Has(entity, component)) ? (byte)1 : (byte)0;
                }
                break;
            }
            case ModAbi.ToolCount:
                U32((uint)world.Store(Component(r.U32())).Count);
                break;
            case ModAbi.ToolProto:
            {
                var mode = r.Byte();
                if (mode == 0)
                {
                    var proto = host.Content.FindProto(Encoding.UTF8.GetString(r.Rest()));
                    BinaryPrimitives.WriteInt32LittleEndian(Room(4), proto?.Id ?? -1);
                }
                else
                {
                    var index = r.U32();
                    if (index < (uint)host.Content.Protos.Count)
                        Text(host.Content.Protos[(int)index].Path);
                }
                break;
            }
            case ModAbi.ToolText:
            {
                var index = r.U32();
                Text(world.Strings.Text(index));
                break;
            }
            case ModAbi.ToolStringId:
            {
                var text = Encoding.UTF8.GetString(r.Rest());
                BinaryPrimitives.WriteInt32LittleEndian(Room(4), world.Strings.Find(text) is { } found ? (int)found : -1);
                break;
            }
            case ModAbi.ToolStorage:
            {
                if (mod is null || mod.StorageEntity == 0)
                    return ModAbi.ErrorUnknownId;
                U32(mod.StorageEntity);
                var bytes = world.GetBytes(mod.StorageEntity, host.StorageComponent, host.StorageData);
                bytes.CopyTo(Room(bytes.Length));
                break;
            }
            default:
                return ModAbi.ErrorUnknownId;
        }
        return 0;
    }

    /// <summary>Местность тайла: вид (0 болото, 1 песок, 2 скала, 3 гора), биом, подножие обрыва (как ctx:terrain).</summary>
    public static (byte Terrain, byte Biome, bool Foot) Terrain(Land land, double x, double y) =>
        ((byte)land.TerrainAt(x, y), (byte)land.BiomeAt(x, y), land.IsCliffFoot(x, y));

    /// <summary>
    /// Расталкивание (ctx:separate из crates/world/src/host.rs): пары корпусов ближе суммы радиусов расходятся на долю
    /// strength перекрытия; стоящий (moving = 0) уступает идущему втрое охотнее; совпавшие точно — разводятся по
    /// номеру строки. Плоская хеш-сетка с клеткой в два наибольших радиуса. x и y меняются на месте.
    /// </summary>
    public static void Separate(Span<double> x, Span<double> y, ReadOnlySpan<double> radius, ReadOnlySpan<double> moving, double strength)
    {
        var n = x.Length;
        if (n == 0)
            return;
        var largest = 0.25;
        foreach (var r in radius[..n])
            largest = Math.Max(largest, r);
        var cell = largest * 2;
        var size = (int)Math.Max(16, BitOperations.RoundUpToPowerOf2((uint)(n * 2)));
        var mask = size - 1;
        var cx = new int[n];
        var cy = new int[n];
        var head = new int[size];
        var next = new int[n];
        head.AsSpan().Fill(-1);
        static int Bucket(int x, int y, int mask) => (int)(((uint)x * 73856093u) ^ ((uint)y * 19349663u)) & mask;
        for (var i = 0; i < n; i++)
        {
            cx[i] = (int)Math.Floor(x[i] / cell);
            cy[i] = (int)Math.Floor(y[i] / cell);
            var b = Bucket(cx[i], cy[i], mask);
            next[i] = head[b];
            head[b] = i;
        }
        var dx = new double[n];
        var dy = new double[n];
        for (var i = 0; i < n; i++)
        {
            for (var oy = -1; oy <= 1; oy++)
                for (var ox = -1; ox <= 1; ox++)
                {
                    var (tx, ty) = (cx[i] + ox, cy[i] + oy);
                    for (var j = head[Bucket(tx, ty, mask)]; j >= 0; j = next[j])
                    {
                        var other = j;
                        if (other <= i || cx[other] != tx || cy[other] != ty)
                            continue;
                        var (ex, ey) = (x[other] - x[i], y[other] - y[i]);
                        var reach = radius[i] + radius[other];
                        var squared = ex * ex + ey * ey;
                        if (squared >= reach * reach)
                            continue;
                        var distance = Math.Sqrt(squared);
                        double ux, uy;
                        if (distance > 1e-9)
                            (ux, uy) = (ex / distance, ey / distance);
                        else
                        {
                            var angle = (double)(i * 7 + other * 13);
                            (ux, uy) = (Math.Cos(angle), Math.Sin(angle));
                        }
                        var overlap = (reach - distance) * strength;
                        var wi = !moving.IsEmpty && moving[i] != 0 ? 1.0 : 3.0;
                        var wj = !moving.IsEmpty && moving[other] != 0 ? 1.0 : 3.0;
                        var (si, sj) = (wi / (wi + wj), wj / (wi + wj));
                        dx[i] -= ux * overlap * si;
                        dy[i] -= uy * overlap * si;
                        dx[other] += ux * overlap * sj;
                        dy[other] += uy * overlap * sj;
                    }
                }
        }
        for (var i = 0; i < n; i++)
        {
            x[i] += dx[i];
            y[i] += dy[i];
        }
    }

    private int Component(uint id) => id < (uint)host.World.Schema.Count ? (int)id : throw new ArgumentException($"нет компонента №{id}");

    private static void Grow(ref double[] array, int n)
    {
        if (array.Length < n)
            array = new double[Math.Max(n, array.Length * 2)];
    }

    private Span<byte> Room(int count)
    {
        if (_length + count > _result.Length)
            Array.Resize(ref _result, Math.Max(_length + count, _result.Length * 2));
        var room = _result.AsSpan(_length, count);
        _length += count;
        return room;
    }

    private void U32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Room(4), value);

    private void Doubles(ReadOnlySpan<double> values) => MemoryMarshal.AsBytes(values).CopyTo(Room(values.Length * 8));

    private void Text(string text) => Encoding.UTF8.GetBytes(text, Room(Encoding.UTF8.GetByteCount(text)));

    /// <summary>Вход инструмента: little-endian, обрыв — <see cref="FormatException"/>.</summary>
    private ref struct Input(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        private int _at;

        public byte Byte() => Take(1)[0];
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

        /// <summary>Счёт элементов не длиннее остатка входа (по size байт на элемент).</summary>
        public int Count(int size)
        {
            var count = U32();
            return count <= (uint)((_bytes.Length - _at) / size) ? (int)count : throw new FormatException($"счёт {count} больше входа");
        }

        public void Doubles(Span<double> into)
        {
            var bytes = Take(into.Length * 8);
            if (BitConverter.IsLittleEndian)
                bytes.CopyTo(MemoryMarshal.AsBytes(into));
            else
                for (var at = 0; at < into.Length; at++)
                    into[at] = BinaryPrimitives.ReadDoubleLittleEndian(bytes[(at * 8)..]);
        }

        public ReadOnlySpan<byte> Rest() => Take(_bytes.Length - _at);

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > _bytes.Length - _at)
                throw new FormatException("вход обрывается");
            var part = _bytes.Slice(_at, count);
            _at += count;
            return part;
        }
    }
}
