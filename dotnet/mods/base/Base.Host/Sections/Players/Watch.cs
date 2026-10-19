using System.Buffers.Binary;
using Base.Host.Kernel;
using Base.Shared;
using Base.Shared.Signals;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Players;

/// <summary>
/// Система players/watch (в TS — обход хоста раз в SWEEP_TICKS): о поражении — весть всем, один раз, когда оно случилось;
/// о защите новичка — всем, когда список защищённых поменялся. Кого уже видели, кто уже побеждён и кто под защитой,
/// помнит память мода (<see cref="Key"/>): переживает сохранение, и весть не повторяется. Игрок, который впервые
/// попался на глаза уже побеждённым (счёт без базы — как в начале партии без стартового набора), вести не получает:
/// так в TS множество defeated заводится на создании хоста.
/// </summary>
internal sealed class Watch : ISystem
{
    /// <summary>Ключ памяти раздела.</summary>
    public const string Key = "players.watch";

    /// <summary>Раз во сколько тиков (SWEEP_TICKS).</summary>
    public const int Every = 5;

    public const int BuildingsT = 0, UnitsT = 1;

    private Column _id, _safe, _buildingOwner, _unitOwner, _unitProto;

    /// <summary>Объявление системы: кадр — счета, таблицы — готовые здания и юниты.</summary>
    public static SystemDeclaration Declare(SystemDeclaration declaration) => declaration
        .Query(Names.Player.Name)
        .Table([Names.Building.Name, Names.Owner.Name], [Names.Site.Name])
        .Table(Names.Unit.Name, Names.Owner.Name, Names.Proto.Name)
        .Every(Every);

    public void Bind(SystemBinding binding)
    {
        P.Load(binding.Component);
        _id = binding.Column(Names.Player.Name, Names.Player.Id);
        _safe = binding.Column(Names.Player.Name, Names.Player.Safe);
        _buildingOwner = binding.Table(BuildingsT).Column(Names.Owner.Name, Names.Owner.Player);
        _unitOwner = binding.Table(UnitsT).Column(Names.Owner.Name, Names.Owner.Player);
        _unitProto = binding.Table(UnitsT).Column(Names.Proto.Name, Names.Proto.Id);
    }

    public void Run(SystemContext ctx)
    {
        var call = Call.Of(ctx);
        // Кто ещё в игре: готовое здание или MCV.
        var standing = new HashSet<uint>();
        var buildings = ctx.Table(BuildingsT);
        foreach (var owner in buildings.Column(_buildingOwner))
            standing.Add((uint)owner);
        var units = ctx.Table(UnitsT);
        var owners = units.Column(_unitOwner);
        var protos = units.Column(_unitProto);
        var mcv = Core.UnitProto(Defeat.Mcv);
        for (var row = 0; row < units.Count; row++)
            if (protos[row] == mcv)
                standing.Add((uint)owners[row]);

        var state = State.Of(call);
        var changed = false;
        var ids = ctx.Column(_id);
        var safe = ctx.Column(_safe);
        var sheltered = new List<uint>();
        var players = new List<(uint Player, double Safe)>(ctx.Count);
        for (var row = 0; row < ctx.Count; row++)
            players.Add(((uint)ids[row], safe[row]));
        players.Sort((a, b) => a.Player.CompareTo(b.Player));
        foreach (var (player, until) in players)
        {
            if (player == 0)
                continue;
            if (until > ctx.Tick)
                sheltered.Add(player);
            var defeated = !standing.Contains(player);
            if (!state.Known.Contains(player))
            {
                state.Known.Add(player);
                if (defeated)
                    state.Defeated.Add(player);
                changed = true;
                continue;
            }
            if (!defeated || state.Defeated.Contains(player))
                continue;
            state.Defeated.Add(player);
            changed = true;
            ctx.Commands.Signal(DefeatedSignal.Name, MemoryPack.MemoryPackSerializer.Serialize(new DefeatedSignal { Player = player }));
            // Весть в чат один раз на поражение; локальной игре не нужна — игрок один.
            ctx.Commands.Signal(Base.Shared.Orders.ChatCall.AnnounceSignal, Base.Shared.Orders.ChatCall.Announce(player, Base.Shared.Orders.ChatCall.RemoteOnly, "defeated"));
        }
        if (!sheltered.SequenceEqual(state.Sheltered))
        {
            state.Sheltered = sheltered;
            changed = true;
            ctx.Commands.Signal(ShelteredSignal.Name, MemoryPack.MemoryPackSerializer.Serialize(new ShelteredSignal { Players = [.. sheltered] }));
        }
        if (changed)
            state.Save(call);
    }

    /// <summary>Игрок начинает заново: о его новом поражении скажут снова.</summary>
    public static void Forget(Call call, uint player)
    {
        var state = State.Of(call);
        if (state.Defeated.Remove(player))
            state.Save(call);
    }

    /// <summary>Что помнит обход: виденные, побеждённые, защищённые — u32 подряд, три списка с длиной впереди.</summary>
    private sealed class State
    {
        public List<uint> Known = [], Defeated = [], Sheltered = [];

        public static State Of(Call call)
        {
            var state = new State();
            if (Memory.Of(call).Get(Key) is not { } bytes)
                return state;
            var at = 0;
            foreach (var list in (List<uint>[])[state.Known, state.Defeated, state.Sheltered])
            {
                if (at + 4 > bytes.Length)
                    break;
                var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));
                at += 4;
                for (var i = 0; i < count && at + 4 <= bytes.Length; i++, at += 4)
                    list.Add(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at)));
            }
            return state;
        }

        public void Save(Call call)
        {
            var lists = new[] { Known, Defeated, Sheltered };
            var bytes = new byte[lists.Sum(list => 4 + list.Count * 4)];
            var at = 0;
            foreach (var list in lists)
            {
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), list.Count);
                at += 4;
                foreach (var player in list)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), player);
                    at += 4;
                }
            }
            Memory.Of(call).Put(call, Key, bytes);
        }
    }
}
