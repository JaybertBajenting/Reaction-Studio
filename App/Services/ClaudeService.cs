using System.Diagnostics;
using System.Text;

namespace ReactionStudio.Services;

/// <summary>
/// Calls the Claude CLI as a subprocess to generate captions.
/// No API key management needed — uses the already-authenticated claude CLI.
/// </summary>
public static class ClaudeService
{
    public static async Task<string> GenerateCaptionAsync(string clipTitle)
    {
        var prompt = $"You write viral YouTube Shorts reaction hook captions. The clip is titled \"{clipTitle}\". " +
                     "Reply with ONLY the caption text — a punchy 3-6 word hook a reactor would put on screen. " +
                     "No quotes, NO emojis, no hashtags, no explanation. " +
                     "Examples: this guy mastered gravity | bro got cooked | nah how did he do that | hold my drink";

        var psi = new ProcessStartInfo("claude")
        {
            Arguments              = $"-p \"{EscapeArg(prompt)}\"",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        try
        {
            using var proc = Process.Start(psi) ?? throw new Exception("claude not found");
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            var result = Clean(output);
            return string.IsNullOrWhiteSpace(result) ? FallbackCaption(clipTitle) : result;
        }
        catch
        {
            return FallbackCaption(clipTitle);
        }
    }

    // Strip quotes/emojis/hashtags and collapse to one tidy line
    private static string Clean(string s)
    {
        s = s.Trim().Trim('"', '\'');
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (c == '#') break;                       // drop trailing hashtags
            if (c == '\n' || c == '\r') break;         // first line only
            if (c < 0x2000) sb.Append(c);              // keep basic latin/punct, drop emoji/symbols
        }
        return sb.ToString().Trim();
    }

    // Turn a messy filename into a usable hook (drop leading "username said" etc.)
    private static string FallbackCaption(string title)
    {
        title = title.Replace('_', ' ').Replace('-', ' ').Trim();
        int said = title.IndexOf(" said ", StringComparison.OrdinalIgnoreCase);
        if (said >= 0) title = title[(said + 6)..];
        title = Clean(title);
        return title.Length > 42 ? title[..42] : title;
    }

    private static string EscapeArg(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
