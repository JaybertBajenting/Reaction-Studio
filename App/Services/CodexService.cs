using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ReactionStudio.Services;

/// <summary>Normalized (0..1) bounding box of profane on-screen text.</summary>
public record ProfanityBox(double X, double Y, double W, double H);

/// <summary>
/// Calls the Codex CLI (`codex exec`) as a subprocess. Headless, uses the
/// already-authenticated Codex login — no API key needed. Codex can see clip
/// frames via -i, so captions/titles/scripts are based on real content.
/// </summary>
public static class CodexService
{
    // Resolved once: full path to codex.exe (GUI processes don't always inherit PATH).
    private static readonly string Exe = ResolveExe();

    private static string ResolveExe()
    {
        string local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "OpenAI", "Codex", "bin", "codex.exe");
        if (File.Exists(local)) return local;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try
            {
                var c = Path.Combine(dir.Trim(), "codex.exe");
                if (File.Exists(c)) return c;
            }
            catch { }
        }
        return "codex";   // last resort — rely on PATH
    }

    private static void Log(string msg)
    {
        try
        {
            string log = Path.Combine(Path.GetTempPath(), "ReactionStudio", "codex_debug.log");
            File.AppendAllText(log, $"[{DateTime.Now:HH:mm:ss}] {msg}\n");
        }
        catch { }
    }

    // Caption (on-screen hook) + YouTube title in one call to save time.
    // Returns (caption, title); falls back to a cleaned filename if Codex fails.
    public static async Task<(string caption, string title)> GenerateCaptionAndTitleAsync(
        string clipTitle, IEnumerable<string>? images = null)
    {
        string prompt =
            $"This is a YouTube Shorts reaction clip (source titled \"{clipTitle}\"). " +
            (images != null ? "I've attached frames so you can SEE what actually happens - base everything on the frames, not the title. " : "") +
            "Only reference things you can actually see; if unsure of a person's gender use 'they' or 'bro', never guess. " +
            "Reply with EXACTLY two lines and nothing else:\n" +
            "Line 1: a punchy 3-6 word on-screen hook caption (no emojis, no quotes, no hashtags).\n" +
            "Line 2: a YouTube Shorts title under 70 characters (no quotes). " +
            "Do NOT use the word \"reaction\" or \"reacting\" in the title." +
            StyleService.BuildStyleBlock(includeHooks: true);

        string raw = await RunAsync(prompt, images);
        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                       .Select(l => l.Trim())
                       .Where(l => l.Length > 0)
                       .ToList();

        string caption = lines.Count > 0 ? CleanCaption(lines[0]) : "";
        string title   = lines.Count > 1 ? StripReaction(lines[1].Trim('"')) : "";

        if (string.IsNullOrWhiteSpace(caption)) caption = FallbackCaption(clipTitle);
        if (string.IsNullOrWhiteSpace(title))   title   = $"{caption} #shorts";
        return (caption, title);
    }

    // Teleprompter: what to say out loud while reacting, based on the clip frames.
    public static async Task<string> GenerateScriptAsync(IEnumerable<string> images)
    {
        string prompt =
            "The attached frames are IN ORDER from a short video I'm about to react to on camera.\n\n" +
            "STEP 1 - Study the frames carefully and accurately. Work out exactly: who or what is in it, " +
            "what is physically happening, and the setting. Use ONLY details you can clearly see.\n" +
            "ACCURACY RULES (important):\n" +
            "- Do NOT invent objects, people, animals, or actions that aren't visible.\n" +
            "- If you cannot clearly tell a person's gender, use 'they' or 'bro' - NEVER guess 'he' or 'she'.\n" +
            "- Don't describe colors/details you're unsure about.\n\n" +
            "STEP 2 - Now write what I'd ACTUALLY say out loud reacting in the moment, like a real person " +
            "talking to their phone (not scripted jokes):\n" +
            "- 5 to 7 SHORT spoken lines, one reaction per line.\n" +
            "- Real Gen Z energy: casual, hyped, unfinished thoughts, natural slang " +
            "(yo, wait, nah, bro, hold up, okay okay, that's crazy, lowkey, no shot, deadass, sheesh) - don't force them.\n" +
            "- Each line under ~8 words, spontaneous not clever, and it must MATCH what's actually on screen.\n" +
            "- NO emojis, NO numbering, NO stage directions, NO quotes, no narrator commentary.\n" +
            "Output ONLY the spoken lines, one per line." +
            StyleService.BuildStyleBlock(includeHooks: true);
        return await RunAsync(prompt, images);
    }

    // Finds profane on-screen TEXT baked into the clip. Returns a TIGHT box per
    // offending word (empty list if none / detection failed).
    public static async Task<List<ProfanityBox>> DetectProfanityBoxesAsync(IEnumerable<string> images)
    {
        string prompt =
            "Look carefully at these frames from a video. Find any profane/swear WORDS shown as ON-SCREEN " +
            "TEXT baked into the video (f-word, s-word, b-word, slurs, etc.). Spoken words and mild words " +
            "don't count - only visible written profanity.\n\n" +
            "CRITICAL: box ONLY the offending word itself, as TIGHTLY as possible around just that word. " +
            "Do NOT box the whole sentence, line, caption, or any clean words - only the actual swear word(s). " +
            "If several swear words appear in different spots, give one box for each.\n\n" +
            "Respond with ONLY compact JSON, nothing else:\n" +
            "{\"boxes\":[{\"x\":<left>,\"y\":<top>,\"w\":<width>,\"h\":<height>}, ...]} " +
            "with x,y,w,h as decimal fractions 0..1 of the frame.\n" +
            "If there are no profane words: {\"boxes\":[]}";

        string raw = await RunAsync(prompt, images);
        var result = new List<ProfanityBox>();
        try
        {
            int i = raw.IndexOf('{'), j = raw.LastIndexOf('}');
            if (i < 0 || j <= i) return result;
            using var doc = JsonDocument.Parse(raw.Substring(i, j - i + 1));
            if (!doc.RootElement.TryGetProperty("boxes", out var boxes) ||
                boxes.ValueKind != JsonValueKind.Array) return result;

            foreach (var b in boxes.EnumerateArray())
            {
                double x = GetD(b, "x"), y = GetD(b, "y"), w = GetD(b, "w"), h = GetD(b, "h");
                if (w <= 0 || h <= 0 || w > 0.97 || h > 0.5) continue;   // skip absurd boxes
                result.Add(new ProfanityBox(
                    Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1),
                    Math.Clamp(w, 0, 1), Math.Clamp(h, 0, 1)));
            }
        }
        catch { }
        return result;
    }

    // Remove the word "reaction"/"reacting" from a title and tidy leftover spaces/punctuation.
    private static string StripReaction(string title)
    {
        title = System.Text.RegularExpressions.Regex.Replace(
            title, @"\b(reaction|reacting|reacts?)\b", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        title = System.Text.RegularExpressions.Regex.Replace(title, @"\s{2,}", " ");
        title = title.Trim(' ', '-', '|', ':', ',', '–', '—');
        return title;
    }

    private static double GetD(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? d : 0;

    // Spoken commentary voiceover script for the AI-character mode. This is what
    // ElevenLabs will read aloud over the clip, so it must be plain spoken prose.
    public static async Task<string> GenerateCommentaryAsync(string clipTitle, IEnumerable<string> images)
    {
        string prompt =
            "I'm making a short commentary video reacting to this clip with an animated character voiceover. " +
            "Based on the attached frames, write what the character SAYS out loud - SHORT punchy spoken reactions, " +
            "like a real creator who talks in quick bursts (think TikTok/Shorts commentary).\n" +
            "Rules:\n" +
            "- KEEP IT SHORT: 18-28 words TOTAL, max. A few snappy lines, not a paragraph.\n" +
            "- Short bursts with natural pauses - NOT long run-on sentences.\n" +
            "- Casual Gen Z energy; react to what actually happens; add a quick take or joke.\n" +
            "- NO emojis, NO stage directions, NO quotes, no narrator labels. Just the spoken words." +
            StyleService.BuildStyleBlock(includeHooks: true);
        return await RunAsync(prompt, images);
    }

    // Continuous commentary that plays OVER the whole clip (the transformative format):
    // the character's voice runs the ENTIRE clip like live commentary, then a sign-off.
    // Sized to ~fill clipSeconds at a natural ~2.3 words/sec so it isn't a tacked-on tag.
    public static async Task<string> GenerateOverlayCommentaryAsync(
        string clipTitle, double clipSeconds, IEnumerable<string> images)
    {
        int secs   = (int)Math.Round(Math.Clamp(clipSeconds, 5, 55));
        int target = Math.Clamp((int)(secs * 2.3), 14, 130);
        int lo = (int)(target * 0.85), hi = (int)(target * 1.10);

        string prompt =
            $"I'm making a commentary Short. My ANIMATED CHARACTER talks continuously OVER a {secs}-second clip — " +
            "their voice runs the WHOLE time like a creator doing live commentary, reacting as things happen.\n" +
            "Study the attached frames (IN ORDER) and write what the character SAYS out loud, start to finish.\n" +
            "This must be TRANSFORMATIVE commentary, not a play-by-play of what's already obvious on screen:\n" +
            "- Lead with a clear ANGLE or take — an opinion, a hot take, a prediction, a 'here's the thing…', or a " +
            "specific thing you noticed that a casual viewer would miss. Have a POINT, don't just narrate.\n" +
            "- Add VALUE the clip alone doesn't: a quick why/how, context, a callout, a genuine judgment, or a joke that " +
            "lands on a specific moment — so someone watches for YOUR take, not just the clip.\n" +
            "- Build to a PAYOFF and end on one punchy sign-off line (the character's outro).\n" +
            "Style:\n" +
            $"- LENGTH: about {lo}-{hi} words total — it must fill ~{secs}s of talking at a natural pace. No filler.\n" +
            "- Short spoken bursts with natural pauses; casual Gen Z energy; NOT a formal essay.\n" +
            "- React to what's ACTUALLY on screen; don't invent things you can't see; use 'they'/'bro' if unsure of gender.\n" +
            "- NO emojis, NO numbering, NO stage directions, NO quotes, no narrator labels. Just the spoken words." +
            StyleService.BuildStyleBlock(includeHooks: true);
        return await RunAsync(prompt, images);
    }

    /// <summary>Run a raw text prompt through Codex and return its reply.</summary>
    public static Task<string> AskAsync(string prompt) => RunAsync(prompt, null);

    // ── Core subprocess call ────────────────────────────────────────────────
    private static async Task<string> RunAsync(string prompt, IEnumerable<string>? images)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ReactionStudio");
        Directory.CreateDirectory(tempDir);
        string outFile = Path.Combine(tempDir, $"codex_{Guid.NewGuid():N}.txt");

        var args = new StringBuilder("exec --skip-git-repo-check --ephemeral -s read-only ");
        // Force the good model/settings regardless of which config the app process loads.
        args.Append("-m gpt-5.5 -c model_reasoning_effort=\"xhigh\" -c service_tier=\"fast\" ");
        args.Append($"-o \"{outFile}\" ");
        if (images != null)
            foreach (var img in images)
                if (File.Exists(img)) args.Append($"-i \"{img}\" ");
        args.Append('-');   // read the prompt from stdin (avoids -i swallowing it)

        var psi = new ProcessStartInfo(Exe)
        {
            Arguments              = args.ToString(),
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding  = new UTF8Encoding(false),  // codex reads stdin as UTF-8 (no BOM)
            WorkingDirectory       = tempDir
        };

        try
        {
            Log($"exe='{Exe}' args={args}");
            using var p = Process.Start(psi) ?? throw new Exception("Process.Start returned null");
            await p.StandardInput.WriteAsync(prompt);
            p.StandardInput.Close();

            // Drain pipes so the process never blocks
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            string stdout = await stdoutTask;
            string stderr = await stderrTask;

            if (File.Exists(outFile))
            {
                string result = (await File.ReadAllTextAsync(outFile)).Trim();
                try { File.Delete(outFile); } catch { }
                if (result.Length == 0)
                    Log($"empty output. exit={p.ExitCode} stderr={Trunc(stderr)} stdout={Trunc(stdout)}");
                return result;
            }
            Log($"no output file. exit={p.ExitCode} stderr={Trunc(stderr)} stdout={Trunc(stdout)}");
            return "";
        }
        catch (Exception ex)
        {
            Log($"EXCEPTION: {ex.Message}");
            return "";
        }
    }

    private static string Trunc(string s) =>
        string.IsNullOrEmpty(s) ? "(none)" : (s.Length > 500 ? s[..500] : s);

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static string CleanCaption(string s)
    {
        s = s.Trim().Trim('"', '\'');
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (c == '#') break;
            if (c == '\n' || c == '\r') break;
            if (c < 0x2000) sb.Append(c);   // drop emoji/symbols the burn-in font can't render
        }
        return sb.ToString().Trim();
    }

    private static string FallbackCaption(string title)
    {
        title = title.Replace('_', ' ').Replace('-', ' ').Trim();
        int said = title.IndexOf(" said ", StringComparison.OrdinalIgnoreCase);
        if (said >= 0) title = title[(said + 6)..];
        title = CleanCaption(title);
        return title.Length > 42 ? title[..42] : title;
    }
}
