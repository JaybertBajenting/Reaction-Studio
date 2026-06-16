using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ReactionStudio.Services;

public record CaptionWord(string Word, double Start, double End);

/// <summary>
/// Transcribes audio to word-level timestamps via faster-whisper (Python subprocess).
/// </summary>
public static class WhisperService
{
    private const string Python = @"C:\Users\M4tth3w\AppData\Local\Programs\Python\Python313\python.exe";

    public static async Task<List<CaptionWord>> TranscribeWordsAsync(string wavPath)
    {
        var result = new List<CaptionWord>();
        if (!File.Exists(wavPath)) return result;

        string tempDir = Path.Combine(Path.GetTempPath(), "ReactionStudio");
        Directory.CreateDirectory(tempDir);
        // Unique names so concurrent transcriptions don't collide.
        string id         = Guid.NewGuid().ToString("N")[..8];
        string scriptPath = Path.Combine(tempDir, $"whisper_{id}.py");
        string outJson    = Path.Combine(tempDir, $"words_{id}.json");

        string script =
            "import sys, json\n" +
            "from faster_whisper import WhisperModel\n" +
            "audio, out = sys.argv[1], sys.argv[2]\n" +
            "model = WhisperModel('small.en', device='cpu', compute_type='int8')\n" +
            "segments, info = model.transcribe(audio, word_timestamps=True, vad_filter=True)\n" +
            "words = []\n" +
            "for seg in segments:\n" +
            "    for w in (seg.words or []):\n" +
            "        t = w.word.strip()\n" +
            "        if t:\n" +
            "            words.append({'w': t, 's': round(float(w.start), 2), 'e': round(float(w.end), 2)})\n" +
            "with open(out, 'w', encoding='utf-8') as f:\n" +
            "    json.dump(words, f)\n" +
            "print('done', len(words))\n";

        await File.WriteAllTextAsync(scriptPath, script);

        var psi = new ProcessStartInfo(Python)
        {
            Arguments              = $"\"{scriptPath}\" \"{wavPath}\" \"{outJson}\"",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        try
        {
            using var p = Process.Start(psi)!;
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            await so; await se;

            if (File.Exists(outJson))
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(outJson));
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    string w = e.GetProperty("w").GetString() ?? "";
                    double s = e.GetProperty("s").GetDouble();
                    double en = e.GetProperty("e").GetDouble();
                    if (!string.IsNullOrWhiteSpace(w)) result.Add(new CaptionWord(w, s, en));
                }
            }
        }
        catch { }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
            try { File.Delete(outJson); } catch { }
        }
        return result;
    }
}
