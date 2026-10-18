using System.IO;
using System.Speech.Synthesis;

namespace ReactionStudio.Services;

/// <summary>
/// Free offline fallback voice (Windows SAPI). Used when ElevenLabs isn't set up
/// or its call fails, so commentary mode can still produce a full preview video.
/// </summary>
public static class LocalTtsService
{
    public static Task SpeakToWavAsync(string text, string outWav) => Task.Run(() =>
    {
        using var synth = new SpeechSynthesizer();
        try
        {
            // Prefer a female voice if present (closer to typical reactor voices)
            var voice = synth.GetInstalledVoices()
                             .FirstOrDefault(v => v.Enabled &&
                                 v.VoiceInfo.Gender == VoiceGender.Female);
            if (voice != null) synth.SelectVoice(voice.VoiceInfo.Name);
        }
        catch { }

        synth.Rate   = 1;    // slightly snappy
        synth.Volume = 100;
        synth.SetOutputToWaveFile(outWav);
        synth.Speak(text);
        synth.SetOutputToNull();
    });
}
