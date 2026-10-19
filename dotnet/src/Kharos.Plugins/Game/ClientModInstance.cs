using Kharos.Mod.Abi;

namespace Kharos.Plugins.Game;

/// <summary>
/// Клиентский мод (ModLoadInfo.Side = "client"), из которого делаются экземпляры: модуль client.wasm
/// (<see cref="GameModule"/>) или сборка .NET (<see cref="ClrModule"/>, docs/dotnet-n6.md, Н6-6).
/// </summary>
public interface IClientModModule : IModModule
{
    /// <summary>Новый экземпляр клиентского мода: рукопожатие ClientAbi.Version и kh_load сделаны.</summary>
    IClientModInstance CreateClientInstance();
}

/// <summary>
/// Экземпляр клиентского мода — одно и то же в wasm и в CLR: облики (kh_look), банк звуков (kh_bank) и кадр (kh_frame).
/// Ответы верны до следующего вызова. Вызовы — из одного потока.
/// </summary>
public interface IClientModInstance : IDisposable
{
    /// <summary>Имя мода.</summary>
    string ModId { get; }

    /// <summary>Что мод объявил в kh_load: облики, звуки, голоса.</summary>
    ModManifest Manifest { get; }

    /// <summary>Экземпляр негоден (ловушка wasm): выбросить и создать новый. В CLR ловушек нет.</summary>
    bool Faulted { get; }

    /// <summary>Запуск: рантайм мода (или загрузка сборок) и kh_load, мс.</summary>
    double StartupMs { get; }

    /// <summary>Источник kh.random; null — своё зерно экземпляра.</summary>
    Func<double>? RandomSource { get; set; }

    /// <summary>Инструменты движка (kh.tool) для этого экземпляра.</summary>
    ModToolHandler? Tools { get; set; }

    /// <summary>kh_look: поток облика вида Manifest.Looks[look].</summary>
    ReadOnlySpan<byte> Look(int look);

    /// <summary>kh_bank: вариант записи Manifest.Bank[entry] при частоте rate.</summary>
    ReadOnlySpan<byte> Bank(int entry, int variant, int rate);

    /// <summary>kh_frame: вход кадра (ClientAbi) → выход кадра.</summary>
    ReadOnlySpan<byte> Frame(ReadOnlySpan<byte> input);
}
