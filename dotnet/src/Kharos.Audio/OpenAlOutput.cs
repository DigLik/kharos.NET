using System.Diagnostics;
using Silk.NET.OpenAL;

namespace Kharos.Audio;

/// <summary>Настройки вывода в OpenAL Soft.</summary>
public sealed record OpenAlOutputOptions
{
    /// <summary>Кадров в одном буфере очереди (512 — 10,7 мс).</summary>
    public int BufferFrames { get; init; } = 512;

    /// <summary>Буферов в очереди: задержка вывода — BufferFrames · BufferCount.</summary>
    public int BufferCount { get; init; } = 4;

    /// <summary>Устройство; null — по умолчанию.</summary>
    public string? DeviceName { get; init; }

    /// <summary>Как часто пробовать открыть пропавшее устройство, секунды.</summary>
    public double ReopenInterval { get; init; } = 1.0;
}

/// <summary>
/// Вывод в OpenAL Soft: свой аудиопоток считает кванты движка и подаёт их потоковыми буферами в один источник (очередь
/// из BufferCount буферов). Источник опустел — недобор: буферы доливаются, источник перезапускается. Устройство
/// пропало (ALC_EXT_disconnect) — движок считается дальше в темпе часов, устройство переоткрывается раз в
/// ReopenInterval. Библиотека подключается динамически (LGPL, docs/dotnet.md, Р9).
/// </summary>
public sealed unsafe class OpenAlOutput : IDisposable
{
    private const int AlcFrequency = 0x1007;
    private const int AlcConnected = 0x313;
    private const int FormatStereoFloat32 = 0x10011;
    private const int AlcAllDevicesSpecifier = 0x1013;

    /// <summary>Сколько выводов открыто: контекст OpenAL общий на процесс, проверка при открытом выводе его бы сбила.</summary>
    private static int _open;

    private readonly AudioEngine _engine;
    private readonly OpenAlOutputOptions _options;
    private readonly float[] _scratch;
    private readonly short[] _pcm;
    private readonly Thread _thread;
    private ALContext? _alc;
    private AL? _al;
    private Device* _device;
    private Context* _context;
    private uint _source;
    private uint[] _buffers = [];
    private bool _float;
    private volatile bool _running;
    private int _underruns;
    private int _reopens;
    private volatile bool _lost;

    private OpenAlOutput(AudioEngine engine, OpenAlOutputOptions options)
    {
        _engine = engine;
        _options = options;
        _scratch = new float[options.BufferFrames * 2];
        _pcm = new short[options.BufferFrames * 2];
        _thread = new Thread(Run) { Name = "Kharos.Audio", IsBackground = true, Priority = ThreadPriority.Highest };
    }

    /// <summary>Имя открытого устройства.</summary>
    public string? DeviceName { get; private set; }

    /// <summary>Сколько раз источник опустел.</summary>
    public int Underruns => Volatile.Read(ref _underruns);

    /// <summary>Сколько раз устройство открывалось заново.</summary>
    public int Reopens => Volatile.Read(ref _reopens);

    /// <summary>Устройство пропало и ещё не вернулось.</summary>
    public bool DeviceLost => _lost;

    /// <summary>Задержка вывода, секунды.</summary>
    public double Latency => _options.BufferFrames * _options.BufferCount / (double)AudioFormat.SampleRate;

    /// <summary>Открыть устройство и запустить аудиопоток; null — устройства или библиотеки нет (error — почему).</summary>
    public static OpenAlOutput? TryOpen(AudioEngine engine, out string? error, OpenAlOutputOptions? options = null)
    {
        options ??= new OpenAlOutputOptions();
        if (options.BufferFrames < AudioFormat.Quantum || options.BufferCount < 2)
            throw new ArgumentOutOfRangeException(nameof(options), "буфер не меньше кванта, буферов не меньше двух");
        if (engine.Output is not null)
            throw new InvalidOperationException("вывод уже подключён");
        var output = new OpenAlOutput(engine, options);
        try
        {
            output._alc = ALContext.GetApi(soft: true);
            output._al = AL.GetApi(soft: true);
        }
        catch (Exception exception)
        {
            error = $"OpenAL Soft не загрузился: {exception.Message}";
            output.Unload();
            return null;
        }
        if (!output.OpenDevice(out error))
        {
            output.Unload();
            return null;
        }
        engine.Output = output;
        Interlocked.Increment(ref _open);
        output._running = true;
        output._thread.Start();
        return output;
    }

    /// <summary>
    /// Проверка без звука: открыть устройство, создать контекст и источник с громкостью 0 без буферов — и сразу закрыть.
    /// Только когда ни один вывод не открыт.
    /// </summary>
    public static bool Probe(out string? deviceName, out string? error)
    {
        deviceName = null;
        if (Volatile.Read(ref _open) > 0)
        {
            error = "вывод уже открыт";
            return false;
        }
        ALContext alc;
        AL al;
        try
        {
            alc = ALContext.GetApi(soft: true);
            al = AL.GetApi(soft: true);
        }
        catch (Exception exception)
        {
            error = $"OpenAL Soft не загрузился: {exception.Message}";
            return false;
        }
        using (alc)
        using (al)
        {
            var device = alc.OpenDevice(null!);
            if (device == null)
            {
                error = "нет устройства вывода";
                return false;
            }
            try
            {
                var attributes = stackalloc int[] { AlcFrequency, AudioFormat.SampleRate, 0 };
                var context = alc.CreateContext(device, attributes);
                if (context == null)
                {
                    error = "контекст не создан";
                    return false;
                }
                try
                {
                    alc.MakeContextCurrent(context);
                    deviceName = Name(alc, device);
                    var source = al.GenSource();
                    al.SetSourceProperty(source, SourceFloat.Gain, 0f);
                    al.DeleteSource(source);
                    var failure = al.GetError();
                    error = failure == AudioError.NoError ? null : $"ошибка OpenAL {failure}";
                    return failure == AudioError.NoError;
                }
                finally
                {
                    alc.MakeContextCurrent(null);
                    alc.DestroyContext(context);
                }
            }
            finally
            {
                alc.CloseDevice(device);
            }
        }
    }

    public void Dispose()
    {
        if (_running)
        {
            _running = false;
            _thread.Join();
            Interlocked.Decrement(ref _open);
        }
        CloseDevice();
        Unload();
        if (_engine.Output == this)
            _engine.Output = null;
    }

    private void Unload()
    {
        _al?.Dispose();
        _alc?.Dispose();
        _al = null;
        _alc = null;
    }

    private bool OpenDevice(out string? error)
    {
        var alc = _alc!;
        var al = _al!;
        _device = alc.OpenDevice(_options.DeviceName!);
        if (_device == null)
        {
            error = "нет устройства вывода";
            return false;
        }
        var attributes = stackalloc int[] { AlcFrequency, AudioFormat.SampleRate, 0 };
        _context = alc.CreateContext(_device, attributes);
        if (_context == null || !alc.MakeContextCurrent(_context))
        {
            error = "контекст OpenAL не создан";
            CloseDevice();
            return false;
        }
        DeviceName = Name(alc, _device);
        _float = al.IsExtensionPresent("AL_EXT_FLOAT32");
        _source = al.GenSource();
        al.SetSourceProperty(_source, SourceBoolean.SourceRelative, true);
        al.SetSourceProperty(_source, SourceVector3.Position, 0f, 0f, 0f);
        al.SetSourceProperty(_source, SourceFloat.Gain, 1f);
        _buffers = al.GenBuffers(_options.BufferCount);
        var failure = al.GetError();
        if (failure != AudioError.NoError)
        {
            error = $"ошибка OpenAL {failure}";
            CloseDevice();
            return false;
        }
        error = null;
        return true;
    }

    private void CloseDevice()
    {
        if (_alc is not { } alc || _al is not { } al)
            return;
        if (_context != null)
        {
            if (_source != 0)
            {
                al.SourceStop(_source);
                al.SetSourceProperty(_source, SourceInteger.Buffer, 0);
                al.DeleteSource(_source);
                _source = 0;
            }
            if (_buffers.Length > 0)
            {
                al.DeleteBuffers(_buffers);
                _buffers = [];
            }
            alc.MakeContextCurrent(null);
            alc.DestroyContext(_context);
            _context = null;
        }
        if (_device != null)
        {
            alc.CloseDevice(_device);
            _device = null;
        }
    }

    /// <summary>Имя открытого устройства (у OpenAL Soft — ALC_ALL_DEVICES_SPECIFIER, DEVICE_SPECIFIER даёт «OpenAL Soft»).</summary>
    private static string? Name(ALContext alc, Device* device)
    {
        var name = alc.GetContextProperty(device, (GetContextString)AlcAllDevicesSpecifier);
        return string.IsNullOrEmpty(name) ? alc.GetContextProperty(device, GetContextString.DeviceSpecifier) : name;
    }

    private bool Connected()
    {
        var connected = 1;
        _alc!.GetContextProperty(_device, (GetContextInteger)AlcConnected, 1, &connected);
        return connected != 0;
    }

    private void Fill(uint buffer)
    {
        _engine.RenderInternal(_scratch);
        var al = _al!;
        if (_float)
        {
            fixed (float* data = _scratch)
                al.BufferData(buffer, (BufferFormat)FormatStereoFloat32, data, _scratch.Length * sizeof(float), AudioFormat.SampleRate);
        }
        else
        {
            for (var i = 0; i < _scratch.Length; i++)
                _pcm[i] = (short)Math.Clamp(_scratch[i] * 32767f, -32768f, 32767f);
            fixed (short* data = _pcm)
                al.BufferData(buffer, BufferFormat.Stereo16, data, _pcm.Length * sizeof(short), AudioFormat.SampleRate);
        }
    }

    private void Prime()
    {
        var al = _al!;
        foreach (var buffer in _buffers)
        {
            Fill(buffer);
            var id = buffer;
            al.SourceQueueBuffers(_source, 1, &id);
        }
        al.SourcePlay(_source);
    }

    private void Run()
    {
        var bufferSeconds = _options.BufferFrames / (double)AudioFormat.SampleRate;
        var nap = Math.Max(1.0, bufferSeconds * 1000 / 4);
        using var napper = new Napper();
        var clock = Stopwatch.StartNew();
        var lostAt = 0.0;
        var lostFrames = 0L;
        var lastTry = 0.0;
        var checkedAt = 0.0;
        _alc!.MakeContextCurrent(_context);
        Prime();
        while (_running)
        {
            var now = clock.Elapsed.TotalSeconds;
            if (!_lost && now - checkedAt > 0.1)
            {
                checkedAt = now;
                if (!Connected())
                {
                    _lost = true;
                    CloseDevice();
                    lostAt = now;
                    lostFrames = 0;
                    lastTry = now;
                }
            }
            if (_lost)
            {
                // Устройства нет: голоса живут дальше в темпе часов.
                var due = (long)((now - lostAt) * AudioFormat.SampleRate);
                while (due - lostFrames >= _options.BufferFrames)
                {
                    _engine.RenderInternal(_scratch);
                    lostFrames += _options.BufferFrames;
                }
                if (now - lastTry >= _options.ReopenInterval)
                {
                    lastTry = now;
                    if (OpenDevice(out _))
                    {
                        _lost = false;
                        Interlocked.Increment(ref _reopens);
                        Prime();
                    }
                }
                napper.Sleep(nap);
                continue;
            }
            var al = _al!;
            al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out var processed);
            for (; processed > 0; processed--)
            {
                uint buffer;
                al.SourceUnqueueBuffers(_source, 1, &buffer);
                Fill(buffer);
                al.SourceQueueBuffers(_source, 1, &buffer);
            }
            al.GetSourceProperty(_source, GetSourceInteger.SourceState, out var state);
            if (state != (int)SourceState.Playing)
            {
                Interlocked.Increment(ref _underruns);
                al.SourcePlay(_source);
            }
            napper.Sleep(nap);
        }
    }
}
