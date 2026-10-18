using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Players;

/// <summary>
/// Раздел Players (С9). Игроки: счета и старт партии, союзы, защита новичка, поражение, редактор.
/// Объявит: приказы surrender, scuttle, ping, ally, breakAlly, exitAlly, edit.*; события player_joined и др.. Системы — через <see cref="Order.System"/> (место в тике — из <see cref="Order.Tick"/>).
/// </summary>
internal static class PlayersSection
{
    public static void Setup(ModBuilder mod)
    {
        // Ждёт С9.
        _ = mod;
    }
}
