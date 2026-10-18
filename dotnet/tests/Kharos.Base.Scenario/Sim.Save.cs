using Kharos.Engine.World;
using KHost = Kharos.Host.Host;
using KHostOptions = Kharos.Host.HostOptions;

namespace Kharos.Base.Scenario;

// Сохранение партии: Host.Save → Host.Load — то же, что throughJson(sim.save()) в TS (данные переживают диск).
public sealed partial class Sim
{
    /// <summary>Сохранение всей партии (мир, слои, земля, игроки, тик, случайный ряд, память модов) — файл KHSV.</summary>
    public byte[] Save() => Host.Save();

    /// <summary>
    /// Новая партия из сохранения этой: тот же режим мода и параметры; исходная остаётся жить (сравнивать их
    /// <see cref="SameWorld"/>, дальше крутить обе). Закрывать новую — вызывающему.
    /// </summary>
    public Sim ThroughSave() => Load(Save());

    /// <summary>Партия из готового сохранения (<see cref="Save"/> или файл) с параметрами этой.</summary>
    public Sim Load(byte[] save)
    {
        var log = new List<string>();
        var options = new KHostOptions
        {
            ModMode = _hostOptions.ModMode,
            Modules = _hostOptions.Modules,
            Log = log.Add,
        };
        var host = KHost.Load(Content(), save, options);
        var sim = new Sim(host, _options, options, log) { _ticks = _ticks };
        sim.Throw();
        return sim;
    }

    /// <summary>Мир двоичным сохранением (WorldSave): сравнение «до байта».</summary>
    public byte[] WorldBytes() => WorldSave.Save(World);

    /// <summary>Совпал ли мир, тик и случайный ряд с другой партией до байта (режимы CLR и wasm, до и после сохранения).</summary>
    public bool SameWorld(Sim other) =>
        Host.Tick == other.Host.Tick
        && Host.Scheduler.RandomState == other.Host.Scheduler.RandomState
        && WorldBytes().AsSpan().SequenceEqual(other.WorldBytes());
}
