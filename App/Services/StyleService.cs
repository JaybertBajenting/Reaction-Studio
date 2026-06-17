using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReactionStudio.Services;

public record StyleExample(string Title, string Hook);
public record StyleProfile(string Channel, string LearnedAt, List<StyleExample> Examples);

/// <summary>
/// Learns a "style profile" from a channel's MOST-POPULAR Shorts (titles + spoken
/// openers), saved to disk and fed to Codex so generated titles/captions/scripts
/// match what actually performs on that channel. Metadata + auto-captions only —
/// no videos are downloaded.
/// </summary>
public static class StyleService
{
    private const string Python = @"C:\Users\M4tth3w\AppData\Local\Programs\Python\Python313\python.exe";

    public static string ProfilePath => Path.Combine(AppPaths.Base, "style_profile.json");

    public static StyleProfile? Load()
    {
        try
        {
            if (File.Exists(ProfilePath))
                return JsonSerializer.Deserialize<StyleProfile>(File.ReadAllText(ProfilePath));
        }
        catch { }
        return null;
    }

    public static void Clear()
    {
        try { File.Delete(ProfilePath); } catch { }
    }

    public static async Task<int> LearnFromChannelAsync(string channelUrl, int topN,
                                                        IProgress<string> progress)
    {
        string url = NormalizeShortsUrl(channelUrl);
        string tempDir = Path.Combine(Path.GetTempPath(), "ReactionStudio", "styleref");
        Directory.CreateDirectory(tempDir);
        foreach (var f in Directory.GetFiles(tempDir)) { try { File.Delete(f); } catch { } }

        // 1) Pull a big batch with view counts (fast, no downloads), then rank.
        //    yt-dlp prints "\t" literally, so use a safe delimiter we can split on.
        progress.Report("Fetching Shorts list…");
        var listOut = await RunPyAsync(
            $"-m yt_dlp --flat-playlist -I 1:300 --print \"%(view_count)s|||%(id)s|||%(title)s\" \"{url}\"");

        var entries = new List<(long views, string id, string title)>();
        foreach (var line in listOut.Split('\n'))
        {
            var parts = line.Split(new[] { "|||" }, StringSplitOptions.None);
            if (parts.Length < 3) continue;
            long.TryParse(parts[0].Trim(), out long v);
            string id = parts[1].Trim();
            string title = string.Join("|||", parts.Skip(2)).Trim();   // rejoin if title had the token
            if (id.Length > 0) entries.Add((v, id, title));
        }
        if (entries.Count == 0) throw new Exception("No Shorts found for that channel URL.");

        var top = entries.OrderByDescending(e => e.views).Take(topN).ToList();

        // 2) Grab spoken auto-captions for the top videos in ONE yt-dlp call (no video download).
        progress.Report($"Reading transcripts of top {top.Count}…");
        var urls = string.Join(" ", top.Select(t => $"\"https://www.youtube.com/watch?v={t.id}\""));
        await RunPyAsync(
            $"-m yt_dlp --skip-download --write-auto-subs --sub-langs \"en.*\" --sub-format vtt " +
            $"-o \"{tempDir}\\%(id)s.%(ext)s\" {urls}");

        // 3) Build examples: title + cleaned spoken opener.
        var examples = new List<StyleExample>();
        foreach (var t in top)
        {
            string hook = "";
            var vtt = Directory.GetFiles(tempDir, t.id + "*.vtt").FirstOrDefault();
            if (vtt != null) hook = FirstWords(CleanVtt(File.ReadAllText(vtt)), 22);
            examples.Add(new StyleExample(t.title, hook));
        }

        string channelName = NiceChannelName(channelUrl);
        var profile = new StyleProfile(channelName, DateTime.Now.ToString("yyyy-MM-dd"), examples);
        File.WriteAllText(ProfilePath,
            JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));

        progress.Report($"Learned {examples.Count} from {channelName}");
        return examples.Count;
    }

    /// <summary>Compact style block for Codex prompts (empty if no profile).</summary>
    public static string BuildStyleBlock(bool includeHooks)
    {
        var p = Load();
        if (p == null || p.Examples.Count == 0) return "";

        var sb = new StringBuilder();
        sb.Append($"\n\nSTYLE REFERENCE - these are top-performing Shorts from a similar channel ({p.Channel}). " +
                  "Match their hook style, energy, and phrasing (do NOT copy them verbatim or mention them):\n");
        sb.Append("Top titles:\n");
        foreach (var e in p.Examples.Take(25))
            sb.Append($"- {e.Title}\n");
        sb.Append("IMPORTANT: match the VARIETY of how they open titles - they rarely start two titles the same way " +
                  "(mix of \"He...\", \"That was...\", \"I...\", \"The...\", \"She...\", \"Why...\", plain observations). " +
                  "Do NOT lean on one opener word like \"Bro\" - only use it occasionally, the way they do.\n");

        if (includeHooks)
        {
            var hooks = p.Examples.Where(e => e.Hook.Length > 0).Take(10).ToList();
            if (hooks.Count > 0)
            {
                sb.Append("How they open (spoken):\n");
                foreach (var e in hooks) sb.Append($"- {e.Hook}\n");
            }
        }
        return sb.ToString();
    }

    // ── helpers ────────────────────────────────────────────────────────────
    private static string NormalizeShortsUrl(string raw)
    {
        raw = raw.Trim();
        if (raw.Contains("/shorts", StringComparison.OrdinalIgnoreCase)) return raw;
        raw = raw.TrimEnd('/');
        // strip a trailing tab like /videos or /featured
        raw = Regex.Replace(raw, @"/(videos|featured|streams|community)$", "",
                            RegexOptions.IgnoreCase);
        return raw + "/shorts";
    }

    private static string NiceChannelName(string url)
    {
        var m = Regex.Match(url, @"@[\w.\-]+");
        return m.Success ? m.Value : "reference channel";
    }

    private static string CleanVtt(string vtt)
    {
        var seen = new List<string>();
        foreach (var raw in vtt.Split('\n'))
        {
            var l = raw.Trim();
            if (l.Length == 0 || l.Contains("-->") || l.StartsWith("WEBVTT") ||
                l.StartsWith("Kind:") || l.StartsWith("Language:") || Regex.IsMatch(l, @"^\d+$"))
                continue;
            l = Regex.Replace(l, @"<[^>]+>", "");        // inline timing tags
            l = WebUtility.HtmlDecode(l);                // &gt; &#39; etc.
            l = l.Replace(">>", " ").Trim();             // speaker markers
            l = Regex.Replace(l, @"\s{2,}", " ");
            if (l.Length == 0) continue;
            if (seen.Count == 0 || seen[^1] != l) seen.Add(l);   // drop rolling-duplicate lines
        }
        return string.Join(" ", seen);
    }

    private static string FirstWords(string text, int n)
    {
        var w = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return w.Length <= n ? text.Trim() : string.Join(" ", w.Take(n)) + "…";
    }

    private static async Task<string> RunPyAsync(string args)
    {
        var psi = new ProcessStartInfo(Python)
        {
            Arguments              = args,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        await se;
        return await so;   // ignore exit code (yt-dlp warns about impersonation but still works)
    }
}
