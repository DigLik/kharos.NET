using Kharos.Audio;
using Kharos.Audio.Graph;
using Kharos.Content;
using Kharos.Mod.Abi;
using Kharos.Plugins.Game;

namespace Kharos.Client.Game;

/// <summary>Звук партии: банк и голоса клиентских модов.</summary>
public sealed partial class ClientGame
{
    // Владелец — Д5 (docs/dotnet-n6.md, Н6.5): звук движка — банк и голоса модов.

    private AudioPrefs? _audioPrefs;

    /// <summary>
    /// Громкости игрока (общая, выключение, шины) из хранилища клиента; применяются к звуку при первом обращении и при
    /// загрузке мода. Без звука (<see cref="Audio"/> = null) значения всё равно читаются и пишутся.
    /// </summary>
    public AudioPrefs SoundPrefs => _audioPrefs ??= new AudioPrefs(Audio, Tools.Storage.Get, (key, value) => Tools.Storage.Set(key, value));

    /// <summary>Банк звуков и голоса мода из манифеста — в звук клиента (без звука — ничего).</summary>
    private void LoadSounds(ModPackage package, IClientModInstance instance, ModManifest manifest)
    {
        _ = SoundPrefs;
        if (Audio is { } audio)
        {
            for (var entry = 0; entry < manifest.Bank.Length; entry++)
            {
                var info = manifest.Bank[entry];
                var variants = new SampleBuffer[Math.Max(info.Variants, 1)];
                for (var variant = 0; variant < variants.Length; variant++)
                    variants[variant] = ReadSample(instance.Bank(entry, variant, AudioFormat.SampleRate), $"мод {package.Name}, звук {info.Name}");
                audio.Bank.Add(info.Name, Buses.Parse(info.Bus) ?? Bus.World, variants);
            }
            foreach (var voice in manifest.Voices)
            {
                try
                {
                    audio.RegisterVoice(voice.Name, VoiceGraph.Decode(voice.Graph));
                }
                catch (FormatException error)
                {
                    Report($"мод {package.Name}, голос {voice.Name}: {error.Message}");
                }
            }
        }
    }

    /// <summary>Запись банка из kh_bank: channels:u8 frames:u32 и f32 канал за каналом.</summary>
    private static SampleBuffer ReadSample(ReadOnlySpan<byte> bytes, string where)
    {
        var r = new AbiReader(bytes);
        int channels = r.U8(), frames = checked((int)r.U32());
        if (channels is 0 or 3 or > 4 || frames > AudioFormat.SampleRate * 60)
            throw new ClientException($"{where}: {channels} каналов, {frames} кадров");
        var data = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            data[c] = new float[frames];
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(r.Take(frames * 4)).CopyTo(data[c]);
        }
        return new SampleBuffer(data);
    }
}
