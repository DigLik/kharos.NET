using Kharos.Audio;
using Kharos.Content;
using Kharos.Mod.Abi;
using Kharos.Plugins.Game;

namespace Kharos.Client.Game;

/// <summary>
/// Клиентский мод в партии: модуль (client.wasm или сборка .NET в режиме CLR — <see cref="ClientGameOptions.ModMode"/>),
/// экземпляр (после ловушки — новый) и его голоса.
/// </summary>
public sealed class ClientModule : IDisposable
{
    private readonly Action<IClientModInstance> _prepare;
    private IClientModInstance? _instance;

    internal ClientModule(ModPackage package, IClientModModule module, Action<IClientModInstance> prepare, AudioEngine? audio)
    {
        Package = package;
        Module = module;
        _prepare = prepare;
        Sounds = new ModSounds(audio);
        _instance = module.CreateClientInstance();
        prepare(_instance);
        Manifest = _instance.Manifest;
    }

    public string Name => Package.Name;
    public ModPackage Package { get; }
    public IClientModModule Module { get; }

    /// <summary>Как исполняется код мода: client.wasm или сборка .NET.</summary>
    public ModMode Mode => Module is ClrModule ? ModMode.Clr : ModMode.Wasm;

    /// <summary>Что мод объявил в kh_load: облики, звуки, голоса.</summary>
    public ModManifest Manifest { get; }

    public ModSounds Sounds { get; }

    /// <summary>Сколько раз экземпляр пересоздан после ловушки.</summary>
    public int Restarts { get; private set; }

    /// <summary>Время kh_frame с разбором выхода прошлого кадра, мс.</summary>
    public double LastFrameMs { get; internal set; }

    /// <summary>Выключен после трёх сбоев.</summary>
    public bool Disabled => Module.Disabled;

    /// <summary>Экземпляр для вызовов (null — выключен).</summary>
    internal IClientModInstance? Acquire()
    {
        if (Module.Disabled)
            return null;
        if (_instance is { Faulted: false })
            return _instance;
        _instance?.Dispose();
        _instance = null;
        Sounds.ReleaseAll();
        _instance = Module.CreateClientInstance();
        _prepare(_instance);
        Restarts++;
        return _instance;
    }

    /// <summary>
    /// Модуль клиентской части пакета в нужном режиме; у пакета её нет — null. CLR: сборка из clrAssemblies или рядом с
    /// mod.json (проект *.Client, <see cref="ClrModule.FindClient"/>); нет её у мода с client.wasm — ошибка (мод потерялся
    /// бы молча). Wasm: client.wasm пакета.
    /// </summary>
    internal static IClientModModule? Load(ModPackage package, ModMode mode, ModLoadInfo load, Func<GameModuleHost> wasmHost, GameModuleOptions options, IReadOnlyDictionary<string, string>? clrAssemblies)
    {
        if (mode == ModMode.Clr)
        {
            var assembly = clrAssemblies?.GetValueOrDefault(package.Name) ?? ClrModule.FindClient(package.Directory);
            if (assembly is null)
                return package.ClientWasm is null ? null : throw new ClientException($"{package.Name}: режим CLR, а сборки клиентской части нет — dotnet build {package.Directory}");
            return new ClrModule(package.Name, assembly, load, options);
        }
        return package.ClientWasm is { } wasm ? wasmHost().Load(package.Name, wasm, load) : null;
    }

    public void Dispose()
    {
        Sounds.ReleaseAll();
        _instance?.Dispose();
        _instance = null;
        Module.Dispose();
    }
}
