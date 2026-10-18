using Kharos.Audio;
using Kharos.Audio.Graph;
using Kharos.Client.Game;
using Kharos.Client.Ui;
using S = Kharos.Mod.Sdk.Client;

namespace Kharos.Client.Tests.Game;

/// <summary>
/// Звук клиентского мода: граф SDK — тот же KHVG, что пишет Kharos.Audio; команды кадра (голос, ручки, place, release,
/// записи банка, петли, громкость шины) доходят до AudioEngine — офлайн-рендер не тихий и повторяется до отсчёта.
/// </summary>
public sealed class AudioStreamTests : IDisposable
{
    private readonly Fonts _fonts = Fonts.Load();

    public void Dispose() => _fonts.Dispose();

    [Fact]
    public void SdkGraphBytesMatchAudioCodec()
    {
        var sdk = new S.VoiceGraphBuilder();
        sdk.SetOut(0.8f);
        var osc = sdk.Osc(S.Wave.Sawtooth, 220, 5);
        var lfo = sdk.Lfo(6, 12);
        lfo.To(osc[S.ParamId.Detune]);
        var filter = sdk.Filter(S.FilterType.Bandpass, 900, 2, 1, 3);
        var env = sdk.Env(0.01f, 0.2f, 0.5f, 0.3f, 0.9f);
        osc.To(filter).To(env).To(sdk.Out);
        sdk.Noise(S.NoiseColor.Pink).To(sdk.Shape([-1f, 0f, 1f])).To(sdk.Delay(0.1f, 1)).To(sdk.Pan(-0.3f)).To(sdk.Out);
        sdk.Sample("bank:a", loop: true, offset: 0.25f, variant: -1, rate: 1.5f, detune: 7).To(sdk.Reverb("bank:ir", normalize: false)).To(sdk.Compressor(-20, 10, 4, 0.01f, 0.2f)).To(sdk.Gain(0.5f)).To(sdk.Out);
        sdk.Const(0.2f).To(env[S.ParamId.Gain]);
        sdk.Shape(0.3f).To(new S.VoiceEndpoint(3, 1, S.ParamId.Gain));
        filter[S.ParamId.Frequency].Set(400, 1).Ramp(1200, 1.5).Exp(300, 2).Target(800, 2.1, 0.05f).Cancel(3);

        var audio = new VoiceGraphBuilder();
        audio.SetOut(new GainSpec(0.8f));
        var aOsc = audio.Osc(Wave.Sawtooth, 220, 5);
        var aLfo = audio.Lfo(6, 12);
        aLfo.To(aOsc[ParamId.Detune]);
        var aFilter = audio.Add(new FilterSpec(FilterType.Bandpass, 900, 2, 1, 3));
        var aEnv = audio.Env(0.01f, 0.2f, 0.5f, 0.3f, 0.9f);
        aOsc.To(aFilter).To(aEnv).To(audio.Out);
        audio.Noise(NoiseColor.Pink).To(audio.Shape([-1f, 0f, 1f])).To(audio.Delay(0.1f, 1)).To(audio.Pan(-0.3f)).To(audio.Out);
        audio.Add(new SampleSpec("bank:a", true, 0.25f, -1, 1.5f, 7)).To(audio.Reverb("bank:ir", normalize: false)).To(audio.Compressor(-20, 10, 4, 0.01f, 0.2f)).To(audio.Gain(0.5f)).To(audio.Out);
        audio.Const(0.2f).To(aEnv[ParamId.Gain]);
        audio.Shape(0.3f).To(new Endpoint(3, 1, ParamId.Gain));
        aFilter[ParamId.Frequency].Set(400, 1).Ramp(1200, 1.5).Exp(300, 2).Target(800, 2.1, 0.05f).Cancel(3);

        var bytes = sdk.Build().Bytes;
        Assert.Equal(audio.Build().Encode(), bytes);
        Assert.Equal(15, VoiceGraph.Decode(bytes).Nodes.Count);
    }

    /// <summary>Кадр мода: команды звука через SDK → разбор клиента → движок звука без устройства.</summary>
    private static float[] Run(int seed)
    {
        var engine = new AudioEngine(new AudioEngineOptions { Seed = (uint)seed });
        var blip = new float[4800];
        for (var i = 0; i < blip.Length; i++)
            blip[i] = MathF.Sin(i * 0.12f) * MathF.Exp(-i / 1200f);
        engine.Bank.Add("blip", Bus.Interface, new SampleBuffer(blip));
        engine.Bank.Add("hum", Bus.World, new SampleBuffer(blip.Select((v, i) => MathF.Sin(i * 0.02f) * 0.3f).ToArray()));
        engine.RegisterVoice("chime", VoiceGraph.Decode(Hello.HelloClient.Chime(660).Bytes));
        var sounds = new ModSounds(engine);
        using var fonts = Fonts.Load();
        var scene = new ClientScene(new Kharos.Client.Ui.Gui(fonts));
        var mod = new S.ClientContext();
        var writer = new AbiWriter();
        var input = new FrameInput();
        var result = new List<float>();
        S.Sound? pad = null;
        for (var frame = 0; frame < 6; frame++)
        {
            var clientInput = new ClientInput { Time = frame / 10.0, Width = 800, Height = 600 };
            scene.Gui.Begin(clientInput.ToUi());
            input.Write(writer, new FrameClock(clientInput.Time, 0.1, (ulong)frame, 0, 0.05, 1), clientInput, scene.Camera, scene.Gui, scene.Atmosphere, [], engine.Now, sounds.TakeEnded());
            var output = mod.Frame(writer.Written, c =>
            {
                Assert.True(c.Audio.Available);
                switch (frame)
                {
                    case 0:
                        c.Audio.Volume(S.Bus.Music, 0.5f);
                        pad = c.Audio.Voice(Hello.HelloClient.Chime(330), new S.VoiceOptions { Bus = S.Bus.Music, Note = 330 });
                        c.Audio.Play("blip", 0.8f, -0.5f, 1.2f);
                        break;
                    case 1:
                        pad!.Set(0, S.ParamId.Gain, 0.5f);
                        pad.Glide(0, S.ParamId.Gain, 0.9f, 0.05f);
                        pad.Place(0.7f, 0.4f);
                        // Голос в узел другого голоса мода: номер мода → номер звучания клиента.
                        var s = new S.VoiceGraphBuilder();
                        s.Osc(S.Wave.Square, 110).To(s.Gain(0.1f)).To(pad.Node(0));
                        c.Audio.Voice(s.Build(), new S.VoiceOptions { Length = 0.2f, Into = pad.Node(0) });
                        c.Audio.Voice("chime", new S.VoiceOptions { Bus = S.Bus.Interface, Length = 0.1f });
                        c.Audio.Loop("hum", 0.6f, 0.2f);
                        break;
                    case 2:
                        c.Sound("blip", c.Camera.X + 1, c.Camera.Y, 1, 0.6f, 0);
                        c.Audio.Voice("нет такого", new S.VoiceOptions());
                        break;
                    case 4:
                        pad!.Release(0.05f);
                        break;
                }
            });
            ClientStream.Apply(output, scene, sounds, new Listener(0, 0, 32, 800, 600));
            result.AddRange(engine.Render(4800).Left);
        }
        Assert.Empty(scene.Errors);
        return [.. result];
    }

    [Fact]
    public void FrameCommandsDriveEngineDeterministically()
    {
        var first = Run(7);
        var second = Run(7);
        Assert.Equal(first.Length, second.Length);
        Assert.True(first.AsSpan().SequenceEqual(second), "один и тот же кадр мода — один и тот же звук");
        var peak = first.Max(MathF.Abs);
        Assert.True(peak > 0.01f, $"тихо: пик {peak}");
        // Голос отпущен в кадре 4: в последнем окне петля фона ещё звучит.
        Assert.True(first.Skip(4800 * 5).Max(MathF.Abs) > 0.001f);
    }

    [Fact]
    public void WithoutEngineCommandsAreSkipped()
    {
        using var fonts = Fonts.Load();
        var scene = new ClientScene(new Kharos.Client.Ui.Gui(fonts));
        var mod = new S.ClientContext();
        var writer = new AbiWriter();
        var clientInput = new ClientInput { Width = 800, Height = 600 };
        scene.Gui.Begin(clientInput.ToUi());
        new FrameInput().Write(writer, default, clientInput, scene.Camera, scene.Gui, scene.Atmosphere, [], null, []);
        var output = mod.Frame(writer.Written, c =>
        {
            Assert.False(c.Audio.Available);
            var voice = c.Audio.Voice(Hello.HelloClient.Chime(440));
            voice.Ramp(0, S.ParamId.Gain, 0, 1);
            voice.Release();
            c.Audio.Play("blip", 1);
        });
        ClientStream.Apply(output, scene, new ModSounds(null));
        ClientStream.Apply(output, scene);
        Assert.Empty(scene.Errors);
    }
}
