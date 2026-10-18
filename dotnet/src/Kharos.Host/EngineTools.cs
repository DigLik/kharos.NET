using Kharos.Engine.Layers;
using Kharos.Engine.Terrain;
using Kharos.Engine.Tools;
using Kharos.Engine.World;
using Kharos.Mod.Abi;

namespace Kharos.Host;

/// <summary>
/// Инструменты движка для модов хоста (kh.tool, форматы — ModAbi.Tool*): общие инструменты <see cref="ModTools"/> над
/// миром партии и память мода (<see cref="ModAbi.ToolStorage"/>). Только чтение мира — каким он был до идущей системы.
/// </summary>
public sealed class EngineTools : ModTools
{
    private readonly Host _host;
    private HostMod? _mod;
    private HostMod? _last;

    public EngineTools(Host host) : base(new HostWorld(host)) => _host = host;

    /// <summary>Вызов инструмента модом mod (null — без мода: тесты). Ответ — длина; места мало — ничего не записано.</summary>
    public int Call(HostMod? mod, int id, ReadOnlySpan<byte> input, Span<byte> output)
    {
        // Отложенный до повтора ответ — только тому же моду (у памяти мода ответ свой).
        if (!ReferenceEquals(mod, _last))
            ForgetPending();
        _last = mod;
        _mod = mod;
        try
        {
            return Call(id, input, output);
        }
        finally
        {
            _mod = null;
        }
    }

    protected override void Report(int id, string message) => _host.Report(_mod, $"инструмент {id}", message, null);

    protected override int Run(int id, ref ToolInput r)
    {
        if (id != ModAbi.ToolStorage)
            return base.Run(id, ref r);
        if (_mod is null || _mod.StorageEntity == 0)
            return ModAbi.ErrorUnknownId;
        U32(_mod.StorageEntity);
        var bytes = _host.World.GetBytes(_mod.StorageEntity, _host.StorageComponent, _host.StorageData);
        bytes.CopyTo(Room(bytes.Length));
        return 0;
    }

    /// <summary>Мир партии для инструментов.</summary>
    private sealed class HostWorld(Host host) : IToolWorld
    {
        public GameWorld World => host.World;
        public Land? Land => host.Land;
        public int ProtoId(string path) => host.Content.FindProto(path)?.Id ?? -1;
        public string? ProtoPath(int id) => (uint)id < (uint)host.Content.Protos.Count ? host.Content.Protos[id].Path : null;
        public LayerSet Layers => host.Layers;
        public ulong Tick => host.Tick;

        public byte Sees(uint player, double x, double y) => host.Vision is not { } vision ? (byte)2
            : vision.Sees(player, x, y) ? (byte)2 : vision.Explored(player, x, y) ? (byte)1 : (byte)0;
    }
}
