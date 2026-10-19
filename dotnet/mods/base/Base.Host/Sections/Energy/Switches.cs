using Base.Host.Kernel;
using Base.Shared.Rules;
using static Base.Host.Sections.Energy.Refs;

namespace Base.Host.Sections.Energy;

/// <summary>
/// Выключатель (setWorking в assembly.ts, приказ work): своё готовое здание — потребитель энергии или реактор — игрок
/// выключает и включает. Выключенное не просит энергии и не работает: начатое стоит, турели молчат, радар видит как
/// обычное здание (метка Off). У завода изделий свой флаг Assembly.on, и он главнее.
/// </summary>
internal static class Switches
{
    /// <summary>Включает или выключает здание; возвращает, изменилось ли что-то.</summary>
    public static bool SetWorking(Call call, uint player, uint entity, bool on)
    {
        if (Books.Kind(call, entity) is not { } kind || !call.Has(entity, S.Building) || call.Has(entity, S.Site) || !call.IsOwn(player, entity)
            || !PowerRules.Switchable(kind.Spec))
            return false;
        var assembly = call.Has(entity, S.Assembly);
        if (assembly ? call.Flag(entity, S.Assembly, R.AssemblyOn) == on : on == !call.Has(entity, R.Off))
            return false;
        if (assembly)
            call.Set(entity, S.Assembly).Flag(R.AssemblyOn, on);
        if (on)
            call.Remove(entity, R.Off);
        else if (!call.Has(entity, R.Off))
            call.Add(entity, R.Off);
        // Обзор выключенного — как у обычного здания (buildingSight с off в vision.ts).
        if (call.Has(entity, S.Sight))
            call.Set(entity, S.Sight).Number(S.SightRadius, Factory.BuildingSight(kind.Type, false, !on || call.Flag(entity, R.Scan, R.ScanDark)));
        return true;
    }
}
