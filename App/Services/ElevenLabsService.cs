using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ReactionStudio.Models;

namespace ReactionStudio.Services;

/// <summary>Turns commentary text into a voiceover MP3 via the ElevenLabs API.</summary>
public static class ElevenLabsService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    public static bool IsConfigured()
    {
        var c = AppConfig.Load();
        return c.ElevenLabsApiKey.Length > 0 && c.ElevenLabsVoiceId.Length > 0;
    }

    /// <summary>Synthesizes <paramref name="text"/> to an MP3 at <paramref name="outMp3"/>.</summary>
    public static async Task TextToSpeechAsync(string text, string outMp3)
    {
        var cfg = AppConfig.Load();
        if (cfg.ElevenLabsApiKey.Length == 0 || cfg.ElevenLabsVoiceId.Length == 0)
            throw new Exception("Set your ElevenLabs API key and Voice ID in Settings first.");

        string url = $"https://api.elevenlabs.io/v1/text-to-speech/{cfg.ElevenLabsVoiceId}";
        var body = new
        {
            text,
            model_id = "eleven_multilingual_v2",
            voice_settings = new { stability = 0.45, similarity_boost = 0.8, style = 0.0, use_speaker_boost = true }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("xi-api-key", cfg.ElevenLabsApiKey);
        req.Headers.Add("Accept", "audio/mpeg");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            string err = await resp.Content.ReadAsStringAsync();
            throw new Exception($"ElevenLabs {(int)resp.StatusCode}: {err[..Math.Min(300, err.Length)]}");
        }

        await using var fs = File.Create(outMp3);
        await resp.Content.CopyToAsync(fs);
    }
}
