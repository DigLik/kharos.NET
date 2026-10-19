using Base.Host.Kernel;
using Base.Shared;
using Base.Shared.Rules;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Energy;

/// <summary>
/// Энергия для клиента (<see cref="GridWire"/>): в конце тика (closeBooks, пока книги открыты) зданиям сетей — Grid
/// (номер сети и биты powerStates, loadedPlants, coreless), сущностям счёта — Grids (хозяйства сетей, только хозяину).
/// Сети считает только хост (зоны, доли, перегруз); клиент по ним рисует молнии, «в работе», знаки нехватки, звучит
/// перегрузом и показывает энергию сети в HUD. Пишется только изменившееся.
/// </summary>
internal static class Publish
{
    private static ComponentRef _grid = null!, _grids = null!;
    private static FieldRef _net, _state, _nets;

    public static void Load(Func<string, ComponentRef> component)
    {
        _grid = component(Names.Grid.Name);
        _grids = component(Names.Grids.Name);
        _net = _grid[Names.Grid.Net];
        _state = _grid[Names.Grid.State];
        _nets = _grids[Names.Grids.Nets];
    }

    /// <param name="tagged">Сущности, у которых Grid уже есть.</param>
    public static void Run(Call call, ReadOnlySpan<uint> tagged)
    {
        var all = Books.All(call);
        var states = Books.States(call);
        var loaded = new HashSet<uint>(Books.Loaded(call));
        var coreless = new HashSet<uint>(Books.Coreless(call));
        var members = new Dictionary<uint, (int Net, int State)>();
        var nets = new Dictionary<uint, List<GridWire.Net>>();
        foreach (var (player, zone, economy) in all)
        {
            if (!nets.TryGetValue(player, out var list))
                nets[player] = list = [];
            list.Add(new GridWire.Net(economy.Overload > 0, Books.HasCore(call, zone), (int)economy.Crowd, economy.Produced, economy.Demand,
                economy.Peak, economy.Stored, economy.Income));
            foreach (var entity in zone.Buildings)
            {
                var state = states.GetValueOrDefault(entity) switch
                {
                    Books.Overload => GridWire.Overload,
                    Books.Starved => GridWire.Starved,
                    _ => 0,
                };
                if (loaded.Contains(entity))
                    state |= GridWire.Loaded;
                if (coreless.Contains(entity))
                    state |= GridWire.Coreless;
                members[entity] = (list.Count, state);
            }
        }

        foreach (var entity in tagged)
            if (!members.ContainsKey(entity) && call.Alive(entity))
                call.Remove(entity, _grid);
        foreach (var (entity, (net, state)) in members)
        {
            if (!call.Alive(entity))
                continue;
            if (!call.Has(entity, _grid))
                call.Add(entity, _grid).Number(_net, net).Number(_state, state);
            else if (call.Num(entity, _grid, _net) != net || call.Num(entity, _grid, _state) != state)
                call.Set(entity, _grid).Number(_net, net).Number(_state, state);
        }

        foreach (var player in Accounts.Players(call).Union(nets.Keys))
        {
            var account = Accounts.Find(call, player);
            if (account == 0 || !call.Alive(account))
                continue;
            var bytes = nets.TryGetValue(player, out var list) ? GridWire.Pack(list) : [];
            if (!call.Has(account, _grids))
                call.Add(account, _grids).Bytes(_nets, bytes);
            else if (!(call.Bytes(account, _grids, _nets) ?? []).AsSpan().SequenceEqual(bytes))
                call.Set(account, _grids).Bytes(_nets, bytes);
        }
    }
}
