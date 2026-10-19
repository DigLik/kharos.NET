namespace Kharos.Balance;

/// <summary>
/// Параллельный прогон харнесса. Сцены независимы: у каждой своя партия (<c>Sim</c>), а код мода в режиме CLR грузится в
/// свою AssemblyLoadContext на партию, так что статика мода у партий своя. Порядок вывода и JSON — как при прогоне по одной.
/// <list type="bullet">
/// <item><see cref="Map{TIn, TOut}"/> — сцены одного куска: считаются разом, результаты — в исходном порядке. Сама сцена
/// ничего не печатает (печать из неё — ошибка): печатает кусок после, по результатам.</item>
/// <item><see cref="Report.Parallel"/> — куски раздела (или разделы <c>all</c>): каждый печатает в свой буфер, буферы
/// сшиваются по порядку.</item>
/// </list>
/// Сцен разом не больше <see cref="Jobs"/> на весь процесс (KHAROS_BALANCE_JOBS, по умолчанию — число ядер): общий семафор
/// держит и память, и ядра, даже когда разделы идут одновременно.
/// </summary>
public static class Work
{
    private static int _jobs = int.TryParse(Environment.GetEnvironmentVariable("KHAROS_BALANCE_JOBS"), out var parsed) && parsed > 0
        ? parsed
        : Environment.ProcessorCount;

    private static SemaphoreSlim _gate = new(_jobs);
    private static readonly AsyncLocal<bool> Inside = new();

    /// <summary>С какой длительности (секунды) сцена попадает в ход расчёта.</summary>
    private const double LongScene = 60;

    static Work() => Widen();

    /// <summary>Сколько сцен считается разом.</summary>
    public static int Jobs
    {
        get => _jobs;
        set
        {
            _jobs = Math.Max(1, value);
            _gate = new SemaphoreSlim(_jobs);
            Widen();
        }
    }

    /// <summary>Идёт ли сейчас сцена (печатать из неё нельзя).</summary>
    public static bool InScene => Inside.Value;

    /// <summary>Сцены, ждущие семафора, держат потоки пула: без запаса пул добавлял бы потоки по одному в полсекунды.</summary>
    private static void Widen()
    {
        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, _jobs * 4 + 16), io);
    }

    /// <summary>Считает <paramref name="run"/> для каждого элемента разом; результаты — в порядке элементов.</summary>
    public static TOut[] Map<TIn, TOut>(IReadOnlyList<TIn> items, Func<TIn, TOut> run)
    {
        var results = new TOut[items.Count];
        // Сцена внутри сцены уже держит место в семафоре: вложенные считаются по очереди, иначе взаимная блокировка.
        if (Inside.Value)
        {
            for (var i = 0; i < items.Count; i++)
                results[i] = run(items[i]);
            return results;
        }
        var gate = _gate;
        void One(int i)
        {
            gate.Wait();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                results[i] = Scene(() => run(items[i]));
            }
            finally
            {
                gate.Release();
                // Долгие сцены — в ход расчёта: по ним видно, что держит весь прогон.
                if (clock.Elapsed.TotalSeconds >= LongScene)
                    Report.Progress($"  сцена {items[i]} — {clock.Elapsed.TotalSeconds:0} с");
            }
        }
        if (_jobs == 1 || items.Count <= 1)
            for (var i = 0; i < items.Count; i++)
                One(i);
        else
            Parallel.For(0, items.Count, new ParallelOptions { MaxDegreeOfParallelism = _jobs }, One);
        return results;
    }

    private static TOut Scene<TOut>(Func<TOut> run)
    {
        Inside.Value = true;
        try
        {
            return run();
        }
        finally
        {
            Inside.Value = false;
        }
    }
}

/// <summary>Сцена с именем для хода расчёта.</summary>
public sealed record Named<T>(string Name, Func<T> Run)
{
    public override string ToString() => Name;
}
