using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Раздел Art: облики всех видов TS из data (на каждый, которого нет в данных, клиент жалуется в журнал) — 18 юнитов и
/// 8 турелей с чертежами из TS, 48 зданий (<see cref="BuildingArt"/>, Art/Buildings: Б-А, Б-Б), месторождения; цвета
/// мини-карты и палитра сторон. TODO(Б-З): Art/Icons.cs, Art/Effects.cs, Art/Paving.cs.
/// </summary>
internal sealed class ArtSection : Section
{
    private bool _palette;

    public override void Setup(ClientBuilder client)
    {
        foreach (var unit in UnitArt.Units)
        {
            var name = unit.Name;
            client.Look("units/" + name, UnitArt.Look(name, () => State.Data.Find("units/" + name)?.Mounts() ?? []) with { Icon = 56 });
        }
        foreach (var (name, _) in UnitArt.Turrets)
            client.Look("turrets/" + name, UnitArt.TurretLook(name));
        foreach (var name in BuildingArt.Names)
        {
            var building = name;
            // Размер и роль берутся из данных, они читаются после Load: облик строится по запросу клиента.
            client.Look("buildings/" + building, () =>
            {
                var kind = State.Data.Find("buildings/" + building);
                return BuildingArt.Look(building, new BuildingInfo((int)(kind?.Number("width", 1) ?? 1), (int)(kind?.Number("height", 1) ?? 1), RoleOf(kind)));
            });
        }
        foreach (var kind in DepositArt.Types)
            client.Look("deposits/" + kind, DepositArt.Look(kind));
        // Месторождения — предметы слоя deposits: поле kind — номер записи в deposits.json (порядок = DepositArt.Types).
        client.ItemLook("deposits", "kind", [.. DepositArt.Types.Select(kind => "deposits/" + kind)]);
        ArtExtras.Setup(client, type => State.Data.Find("units/" + type)?.Mounts() ?? []); // Б-З: значки, эффекты, следы, покрытие.
        // Мини-карта пустыни (lands/desert.luau): цвета по биомам (эрг, солончаки, красные пустоши, топи) —
        // [песок, скала, болото, горы], средние тона палитр terrain.frag (PALETTE в minimap.ts).
        client.Minimap(
            0xC29945, 0x63450F, 0x36331A, 0x8C6828,
            0xD1C9B3, 0x5E5C54, 0x4D8585, 0x828078,
            0xB35C29, 0x542414, 0x452112, 0x804024,
            0x857A38, 0x454021, 0x1A3621, 0x645F34);
        // Палитра сторон — первой на этапе камеры, до самой камеры (как в пилоте).
        client.Camera("base.palette", Frame);
    }

    /// <summary>Палитра сторон партии (TEAMS из unitArt.ts): своя, чужая, союзная, ничья — тёмный, основной, светлый.</summary>
    public override void Frame(ClientContext c)
    {
        if (_palette || Profile is null)
            return;
        _palette = true;
        c.Palette([
            0x1f63d8, 0x5aa9ff, 0xe4f4ff,
            0xb3261e, 0xff6b5a, 0xffe9e4,
            0x1f7a3c, 0x4ec973, 0xe8fff0,
            0x1f63d8, 0x5aa9ff, 0xe4f4ff]);
    }

    /// <summary>Роль здания по записи вида (data/buildings.json): от неё цвет заглушки.</summary>
    private static BuildingKit.Role RoleOf(Kind? kind)
    {
        if (kind is null)
            return BuildingKit.Role.Storage;
        if (kind.Has("produces"))
            return BuildingKit.Role.Production;
        if (kind.Flag("defense") || kind.Has("mounts") || kind.Has("shield"))
            return BuildingKit.Role.Defense;
        if (kind.Number("power") > 0 || kind.Has("battery") || kind.Has("burns"))
            return BuildingKit.Role.Power;
        if (kind.Has("assembles") || kind.Has("limits"))
            return BuildingKit.Role.Processing;
        return BuildingKit.Role.Storage;
    }
}
