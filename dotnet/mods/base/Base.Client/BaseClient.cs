using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>Клиентская часть base. Заготовка: облики и подача придут переносом пилота.</summary>
public sealed class BaseClient : ClientMod
{
    public override void Setup(ClientBuilder client) => AudioSetup.Register(client);
}