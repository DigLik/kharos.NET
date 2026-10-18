using Kharos.Mod.Sdk.Client;

namespace Base.Client.Audio;

/// <summary>
/// Ветер полёта — перенос mods/base/audio/flight.luau (flight.ts): камера несётся над картой — в ушах свистит поток. Чем
/// быстрее, тем громче и выше свист, поток дрожит и рвётся. Скорость камеры — как в soundscape.ts: с FlightStart тайлов в
/// секунду начинается, на FlightFull свистит во всю силу, между ними — по логарифму.
/// </summary>
internal sealed class FlightAudio
{
    /// <summary>Имя голоса в ClientBuilder.Voice.</summary>
    public const string Voice = "base:flight";

    private const float Level = 0.9f;
    private const double Rise = 0.08;
    private const double Fall = 0.35;
    private const float BandLow = 450;
    private const float BandHigh = 4200;
    private const float FlutterLow = 5;
    private const float FlutterHigh = 13;
    private const double FlightStart = 20;
    private const double FlightFull = 500;

    /// <summary>Узлы голоса: куда писать ручки.</summary>
    internal readonly record struct Nodes(int Bus, int Band, int Rush, int Flutter, int FlutterDepth, int SwayDepth);

    /// <summary>Граф голоса и номера его узлов.</summary>
    public static (VoiceGraph Graph, Nodes Nodes) Build()
    {
        var s = new VoiceGraphBuilder();
        var bus = s.Gain(0);
        bus.To(s.Out);
        // Свист — розовый шум через полосу.
        var rush = s.Sample("noise:pink", loop: true, offset: 1.3f);
        var band = s.Filter(FilterType.Lowpass, BandLow, 1.5f);
        var rushGain = s.Gain(1);
        rush.To(s.Filter(FilterType.Highpass, 160, 0.7f)).To(band).To(rushGain).To(bus);
        // Гул — тело потока: бурый шум, только низы.
        s.Sample("noise:brown", loop: true, offset: 2.7f).To(s.Filter(FilterType.Lowpass, 240, 0.7f)).To(s.Gain(0.9f)).To(bus);
        // Трепет — свист дрожит часто и неглубоко; раскачка — весь ветер медленно накатывает.
        var flutter = s.Osc(Wave.Sine, FlutterLow);
        var flutterDepth = s.Gain(0);
        flutter.To(flutterDepth).To(rushGain[ParamId.Gain]);
        var sway = s.Osc(Wave.Sine, 0.6f);
        var swayDepth = s.Gain(0);
        sway.To(swayDepth).To(bus[ParamId.Gain]);
        return (s.Build(), new Nodes(bus.Index, band.Index, rush.Index, flutter.Index, flutterDepth.Index, swayDepth.Index));
    }

    /// <summary>Узлы голоса "base:flight" (граф строится одинаково, номера постоянны).</summary>
    public static Nodes Layout { get; } = Build().Nodes;

    private Sound? _voice;
    private double _level;
    private (double X, double Y)? _last;

    /// <summary>Ручки: level от 0 до 1 — тихий ветер слышен еле-еле, громким становится только быстрый.</summary>
    public static void Update(Sound voice, float level)
    {
        var n = Layout;
        var loudness = Level * MathF.Pow(level, 1.5f);
        voice.Glide(n.Bus, ParamId.Gain, loudness, 0.03f);
        voice.Glide(n.SwayDepth, ParamId.Gain, loudness * 0.2f, 0.03f);
        voice.Glide(n.Band, ParamId.Frequency, BandLow * MathF.Pow(BandHigh / BandLow, level), 0.05f);
        voice.Glide(n.Rush, ParamId.Rate, 0.8f + 0.5f * level, 0.05f);
        voice.Glide(n.Flutter, ParamId.Frequency, FlutterLow + (FlutterHigh - FlutterLow) * level, 0.05f);
        voice.Glide(n.FlutterDepth, ParamId.Gain, 0.3f * level, 0.05f);
    }

    public void Update(ClientContext c)
    {
        if (!c.Audio.Available)
            return;
        if (_voice is null)
        {
            _voice = c.Audio.Voice(Voice, new VoiceOptions { Bus = Bus.World });
            _voice.Place(1, 0);
        }
        var camera = c.Camera;
        var seconds = Math.Max(c.Dt, 1e-3);
        double speed = 0;
        if (_last is { } last)
            speed = Math.Sqrt((camera.X - last.X) * (camera.X - last.X) + (camera.Y - last.Y) * (camera.Y - last.Y)) / seconds;
        _last = (camera.X, camera.Y);
        var target = Math.Clamp(Math.Log(Math.Max(speed, 1e-6) / FlightStart) / Math.Log(FlightFull / FlightStart), 0, 1);
        _level += (target - _level) * (1 - Math.Exp(-seconds / (target > _level ? Rise : Fall)));
        Update(_voice, (float)_level);
    }
}
