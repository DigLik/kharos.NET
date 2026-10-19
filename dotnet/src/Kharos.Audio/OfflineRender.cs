namespace Kharos.Audio;

/// <summary>Отрезок офлайн-рендера: стерео float, кадров = <see cref="Frames"/>.</summary>
public sealed class RenderedAudio(float[] interleaved, int sampleRate)
{
    /// <summary>L, R, L, R…</summary>
    public float[] Interleaved { get; } = interleaved;

    public int SampleRate { get; } = sampleRate;

    public int Frames => Interleaved.Length / 2;

    public double Seconds => Frames / (double)SampleRate;

    public float Left(int frame) => Interleaved[2 * frame];

    public float Right(int frame) => Interleaved[2 * frame + 1];

    public float[] Channel(int channel)
    {
        var data = new float[Frames];
        for (var i = 0; i < data.Length; i++)
            data[i] = Interleaved[2 * i + channel];
        return data;
    }

    /// <summary>Наибольший модуль отсчёта на отрезке кадров [from, from + count).</summary>
    public float Peak(int from = 0, int count = -1)
    {
        var end = count < 0 ? Frames : Math.Min(Frames, from + count);
        var peak = 0f;
        for (var i = from * 2; i < end * 2; i++)
            peak = MathF.Max(peak, MathF.Abs(Interleaved[i]));
        return peak;
    }

    /// <summary>Среднеквадратичное значение (оба канала) на отрезке кадров.</summary>
    public double Rms(int from = 0, int count = -1)
    {
        var end = count < 0 ? Frames : Math.Min(Frames, from + count);
        double sum = 0;
        for (var i = from * 2; i < end * 2; i++)
            sum += Interleaved[i] * (double)Interleaved[i];
        return end > from ? Math.Sqrt(sum / ((end - from) * 2)) : 0;
    }
}

/// <summary>Событие офлайн-сценария: в момент At (секунды контекста) вызвать Do над движком.</summary>
public readonly record struct OfflineEvent(double At, Action<AudioEngine> Do);

/// <summary>
/// Офлайн-рендер графа микшера без устройства. Идёт тем же микшером и тем же путём, что вывод в устройство
/// (<see cref="AudioEngine.Render(Span{float})"/>), поэтому при тех же командах звук тот же сэмпл в сэмпл и не зависит от
/// размера кусков. Команды применяются в начале кванта (128 кадров): события сценария выравниваются вверх до границы
/// кванта — так они легли бы в реальном времени. Для проверок, золотых записей банка и записи роликов.
/// </summary>
public static class OfflineRender
{
    /// <summary>Рендер frames кадров.</summary>
    public static RenderedAudio Frames(AudioEngine engine, int frames)
    {
        var data = new float[checked(frames * 2)];
        engine.Render(data);
        return new RenderedAudio(data, AudioFormat.SampleRate);
    }

    /// <summary>Рендер seconds секунд.</summary>
    public static RenderedAudio Seconds(AudioEngine engine, double seconds) =>
        Frames(engine, (int)Math.Round(seconds * AudioFormat.SampleRate));

    /// <summary>
    /// Сценарий: события по порядку времени, между ними — рендер; всего seconds секунд. Событие раньше текущего кадра
    /// выполняется сразу.
    /// </summary>
    public static RenderedAudio Run(AudioEngine engine, double seconds, params OfflineEvent[] events)
    {
        var total = (int)Math.Round(seconds * AudioFormat.SampleRate);
        var data = new float[checked(total * 2)];
        var done = 0;
        foreach (var e in events.OrderBy(e => e.At))
        {
            var frame = (int)Math.Ceiling(e.At * AudioFormat.SampleRate / AudioFormat.Quantum) * AudioFormat.Quantum;
            frame = Math.Clamp(frame, done, total);
            if (frame > done)
            {
                engine.Render(data.AsSpan(done * 2, (frame - done) * 2));
                done = frame;
            }
            e.Do(engine);
        }
        if (done < total)
            engine.Render(data.AsSpan(done * 2, (total - done) * 2));
        return new RenderedAudio(data, AudioFormat.SampleRate);
    }
}
