namespace Kharos.Mod.Sdk;

/// <summary>Сведения о наборе мододела.</summary>
public static class ModSdkInfo
{
    /// <summary>Версия ABI мода (docs/dotnet.md, §4.2; Abi/ModAbi.cs). Растёт при несовместимых изменениях.</summary>
    public const int AbiVersion = Kharos.Mod.Abi.ModAbi.Version;
}
