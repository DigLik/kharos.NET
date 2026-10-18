using Kharos.Mod.Sdk;
using MemoryPack;

namespace Base.Host.Kernel;

/// <summary>
/// Память мода base (Storage движка, MemoryPack): чего не найти запросом мира — сущность счёта по игроку, счётчики. Живёт в
/// мире, сохраняется и переживает выброс экземпляра. Раздел, которому нужно своё, кладёт байты под своим ключом
/// (<see cref="Get"/>, <see cref="Put"/>). Читается раз на вызов (<see cref="Of"/>), запись — командой сразу.
/// </summary>
[MemoryPackable]
internal sealed partial class Memory
{
    /// <summary>Игроки по порядку заведения счёта.</summary>
    public uint[] Players { get; set; } = [];

    /// <summary>Сущности счёта игроков (по тому же порядку).</summary>
    public uint[] Accounts { get; set; } = [];

    /// <summary>Тик и номер последней группы очереди Shift (nextGroup в orders.ts).</summary>
    public ulong GroupTick { get; set; }
    public int GroupSeq { get; set; }

    /// <summary>Байты разделов по ключу.</summary>
    public Dictionary<string, byte[]> Sections { get; set; } = [];

    /// <summary>Память этого вызова: прочитанная раз, со своими правками.</summary>
    public static Memory Of(Call call)
    {
        if (call.Memory is { } memory)
            return memory;
        var bytes = Tools.ReadStorage(out _);
        memory = bytes is { Length: > 0 } ? MemoryPackSerializer.Deserialize<Memory>(bytes) ?? new Memory() : new Memory();
        call.Memory = memory;
        return memory;
    }

    /// <summary>Пишет память командой (вызов с записью).</summary>
    public void Save(Call call)
    {
        if (call.Writes)
            Tools.WriteStorage(call.Commands, MemoryPackSerializer.Serialize(this));
    }

    /// <summary>Байты раздела; нет — null.</summary>
    public byte[]? Get(string key) => Sections.GetValueOrDefault(key);

    /// <summary>Кладёт байты раздела и пишет память.</summary>
    public void Put(Call call, string key, byte[] bytes)
    {
        Sections[key] = bytes;
        Save(call);
    }
}
