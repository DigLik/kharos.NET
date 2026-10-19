namespace Kharos.Mod.Sdk;

/// <summary>Сведения о наборе мододела.</summary>
public static class ModSdkInfo
{
    /// <summary>Версия ABI мода (docs/dotnet.md, §4.2; Abi/ModAbi.cs). Растёт при несовместимых изменениях.</summary>
    public const int AbiVersion = Kharos.Mod.Abi.ModAbi.Version;

    /// <summary>Версия ABI клиентского мода (Abi/ClientAbi.cs; 2 — Н6). Своя у клиентской стороны.</summary>
    public const int ClientAbiVersion = Kharos.Mod.Abi.ClientAbi.Version;
}
