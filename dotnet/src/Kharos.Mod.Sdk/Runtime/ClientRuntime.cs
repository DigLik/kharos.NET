using System.ComponentModel;
using Kharos.Mod.Abi;
using Kharos.Mod.Sdk.Client;
using MemoryPack;

namespace Kharos.Mod.Sdk.Runtime;

/// <summary>
/// Сторона клиентского мода в ABI (Abi/ClientAbi.cs): kh_load, kh_look, kh_bank, kh_frame. Их пишет за мододела
/// build/ClientExports.cs. Один мод на модуль wasm; вызовы — из одного потока клиента.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static unsafe class ClientRuntime
{
    private static ClientBuilder? _builder;
    private static ClientContext? _client;
    private static readonly ByteWriter Output = new();

    /// <summary>kh_load: разбор ModLoadInfo, Setup и Load мода; в out — ModManifest с обликами и звуками.</summary>
    public static int Load(delegate*<ClientMod> create, int ptr, int len)
    {
        try
        {
            var info = MemoryPackSerializer.Deserialize<ModLoadInfo>(new ReadOnlySpan<byte>((void*)(nint)ptr, len));
            if (info is null)
                return ModRuntime.Fail(ModAbi.ErrorBadInput, "kh_load: пустой ModLoadInfo");
            if (info.Abi != ModAbi.Version)
                return ModRuntime.Fail(ModAbi.ErrorAbi, $"kh_load: ABI клиента {info.Abi}, мода {ModAbi.Version}");
            var mod = create();
            var builder = new ClientBuilder();
            mod.Setup(builder);
            var manifest = builder.ToManifest();
            var schema = new WorldSchema(info.Components);
            var ctx = new LoadContext(info, schema);
            mod.Load(ctx);
            var client = new ClientContext();
            client.Load(ctx, schema);
            _builder = builder;
            _client = client;
            Output.Clear();
            Output.Raw(MemoryPackSerializer.Serialize(manifest));
            return ModRuntime.Done(Output.Pointer, Output.Length);
        }
        catch (Exception ex)
        {
            return ModRuntime.Fail(ex is MemoryPackSerializationException ? ModAbi.ErrorBadInput : ModAbi.ErrorException, $"kh_load: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>kh_look: облик Looks[look] потоком (команды холста всех кадров).</summary>
    public static int Look(int look)
    {
        if (_builder is null)
            return ModRuntime.Fail(ModAbi.ErrorNotLoaded, "kh_look до kh_load");
        if ((uint)look >= (uint)_builder.Looks.Count)
            return ModRuntime.Fail(ModAbi.ErrorUnknownId, $"kh_look: нет облика {look}");
        var (proto, spec) = _builder.Looks[look];
        try
        {
            Output.Clear();
            spec().Write(Output);
        }
        catch (Exception ex)
        {
            return ModRuntime.Fail(ModAbi.ErrorException, $"облик {proto}: {ex.GetType().Name}: {ex.Message}");
        }
        return ModRuntime.Done(Output.Pointer, Output.Length);
    }

    /// <summary>kh_bank: вариант variant записи Bank[entry] при частоте rate.</summary>
    public static int Bank(int entry, int variant, int rate)
    {
        if (_builder is null)
            return ModRuntime.Fail(ModAbi.ErrorNotLoaded, "kh_bank до kh_load");
        if ((uint)entry >= (uint)_builder.Bank.Count)
            return ModRuntime.Fail(ModAbi.ErrorUnknownId, $"kh_bank: нет записи {entry}");
        var (info, render) = _builder.Bank[entry];
        try
        {
            Output.Clear();
            BankWriter.Write(Output, render(rate, variant));
        }
        catch (Exception ex)
        {
            return ModRuntime.Fail(ModAbi.ErrorException, $"звук {info.Name}: {ex.GetType().Name}: {ex.Message}");
        }
        return ModRuntime.Done(Output.Pointer, Output.Length);
    }

    /// <summary>kh_frame: вход кадра → обработчики → выход кадра. Сбой обработчика — в журнал, кадр идёт дальше.</summary>
    public static int Frame(int ptr, int len)
    {
        if (_builder is null || _client is null)
            return ModRuntime.Fail(ModAbi.ErrorNotLoaded, "kh_frame до kh_load");
        try
        {
            var (pointer, length) = _client.Frame(new ReadOnlySpan<byte>((void*)(nint)ptr, len), _builder, message => Sdk.Host.Log(message, LogLevel.Error));
            return ModRuntime.Done((byte*)pointer, length);
        }
        catch (Exception ex)
        {
            return ModRuntime.Fail(ex is FormatException ? ModAbi.ErrorBadInput : ModAbi.ErrorException, $"kh_frame: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
