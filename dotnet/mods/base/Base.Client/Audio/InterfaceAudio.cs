using System.Collections;
using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;

namespace Base.Client.Audio;

/// <summary>
/// Звуки интерфейса — перенос mods/base/audio/interface.luau (interfaceSounds.ts): щелчок по кнопке, касание кнопки при
/// наведении, отказ у недоступной, выделение юнитов и зданий. Приказы звучат там, где их отдают (controls/orders).
/// Выделение — c.Shared["selection"]: перечисление номеров сущностей (его пишет мод управления).
/// </summary>
internal sealed class InterfaceAudio
{
    private const float Volume = 0.35f;
    private const float HoverVolume = 0.12f;
    // Чаще этого выделение не звучит: протянутая рамка меняет его каждый кадр.
    private const double SelectGap = 0.12;
    // Разброс высоты разовых звуков (DETUNE в audio.ts).
    private const double Detune = 0.08;

    private ulong _selected;
    private int _count;
    private double _clock, _selectedAt = double.NegativeInfinity;
    private ComponentRef? _unit;

    private static float Rate(ClientContext c) => (float)(1 + (c.Audio.Random() * 2 - 1) * Detune);

    public void Update(ClientContext c)
    {
        if (!c.Audio.Available)
            return;
        _clock += c.Dt;
        if (c.Ui.PressedButton is { } disabled)
            c.Audio.Play(disabled ? "deny" : "click", Volume, 0, Rate(c));
        else if (c.Ui.HoveredButton)
            c.Audio.Play("hover", HoverVolume, 0, Rate(c));
        // Выделение сменилось и в нём что-то есть — щелчок: у юнитов один, у здания другой. Снятое молчит.
        var selection = c.Shared.TryGetValue("selection", out var value) ? value as IEnumerable : null;
        ulong key = 14695981039346656037;
        var count = 0;
        if (selection is not null)
            foreach (var item in selection)
            {
                key = (key ^ Convert.ToUInt32(item)) * 1099511628211;
                count++;
            }
        if (key == _selected && count == _count)
            return;
        _selected = key;
        _count = count;
        if (count == 0 || _clock - _selectedAt < SelectGap)
            return;
        _selectedAt = _clock;
        _unit ??= c.Component("Unit");
        var units = false;
        foreach (var item in selection!)
            if (c.Has(Convert.ToUInt32(item), _unit))
                units = true;
        c.Audio.Play(units ? "select" : "selectBuilding", Volume, 0, Rate(c));
    }
}
