using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace ReactionStudio.Services;

public static class FfmpegService
{
    public const string Exe = @"D:\Tools\ffmpeg\ffmpeg.exe";

    // ── Info ─────────────────────────────────────────────────────────────────
    public static double GetDuration(string path)
    {
        var r = Run($"-i \"{path}\"");
        var m = Regex.Match(r, @"Duration:\s*(\d+):(\d+):([\d.]+)");
        if (!m.Success) return 0;
        return double.Parse(m.Groups[1].Value) * 3600
             + double.Parse(m.Groups[2].Value) * 60
             + double.Parse(m.Groups[3].Value);
    }

    // ── Video dimensions ───────────────────────────────────────────────────────
    public static (int w, int h) GetDimensions(string path)
    {
        var r = Run($"-i \"{path}\"");
        // The WxH can sit after the colorspace info (which contains its own commas),
        // so scan the whole Video stream description, not just up to the first comma.
        var m = Regex.Match(r, @"Video:[\s\S]*?[\s,](\d{2,5})x(\d{2,5})");
        if (!m.Success) return (0, 0);
        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
    }

    // ── Censor (blur) one or more regions of the clip for its whole duration ───
    // Each box is 0..1 fractions of the frame; only the offending words are blurred.
    public static async Task CensorRegionsAsync(string src, string dst,
                                                IReadOnlyList<(double x, double y, double w, double h)> boxes)
    {
        var (W, H) = GetDimensions(src);
        if (W == 0 || H == 0 || boxes.Count == 0) { File.Copy(src, dst, true); return; }

        // Convert each box to even pixel coords with a small safety margin.
        var px = new List<(int x, int y, int w, int h, int r)>();
        foreach (var b in boxes)
        {
            double mx = b.w * 0.04, my = b.h * 0.10;
            double fx = Math.Clamp(b.x - mx, 0, 1), fy = Math.Clamp(b.y - my, 0, 1);
            double fw = Math.Clamp(b.w + 2 * mx, 0, 1 - fx), fh = Math.Clamp(b.h + 2 * my, 0, 1 - fy);

            int bw = Even((int)(fw * W)), bh = Even((int)(fh * H));
            int bx = Even((int)(fx * W)), by = Even((int)(fy * H));
            if (bw < 2 || bh < 2) continue;
            bw = Math.Min(bw, W - bx); bh = Math.Min(bh, H - by);
            int r = Math.Max(4, Math.Min(bw, bh) / 4);
            px.Add((bx, by, bw, bh, r));
        }
        if (px.Count == 0) { File.Copy(src, dst, true); return; }

        // Split the source into a base + one copy per box, blur each crop, overlay back.
        var fc = new System.Text.StringBuilder();
        fc.Append($"[0:v]split={px.Count + 1}[base]");
        for (int i = 0; i < px.Count; i++) fc.Append($"[s{i}]");
        fc.Append(';');
        for (int i = 0; i < px.Count; i++)
        {
            var b = px[i];
            fc.Append($"[s{i}]crop={b.w}:{b.h}:{b.x}:{b.y},boxblur={b.r}:3:{b.r}:3,boxblur={b.r}:2[bl{i}];");
        }
        string cur = "base";
        for (int i = 0; i < px.Count; i++)
        {
            var b = px[i];
            string outLbl = i == px.Count - 1 ? "outv" : $"o{i}";
            fc.Append($"[{cur}][bl{i}]overlay={b.x}:{b.y}[{outLbl}];");
            cur = outLbl;
        }
        if (fc[^1] == ';') fc.Length--;   // trim trailing ;

        string args = $"-y -i \"{src}\" -filter_complex \"{fc}\" " +
                      "-map [outv] -map 0:a? -c:v libx264 -preset veryfast -crf 22 -c:a copy " +
                      $"\"{dst}\"";
        await RunAsync(args);
    }

    private static int Even(int n) => n % 2 == 0 ? n : n - 1;

    // ── Extract audio ─────────────────────────────────────────────────────────
    public static async Task ExtractAudioAsync(string src, string dst)
    {
        await RunAsync($"-y -i \"{src}\" -vn -ar 44100 -ac 2 \"{dst}\"");
    }

    // ── Extract frame ─────────────────────────────────────────────────────────
    public static async Task ExtractFrameAsync(string src, string dst, double ratio = 0.35)
    {
        double dur = GetDuration(src);
        double t   = Math.Max(0.5, dur * ratio);
        string ts  = TimeSpan.FromSeconds(t).ToString(@"hh\:mm\:ss\.fff");
        await RunAsync($"-y -ss {ts} -i \"{src}\" -vframes 1 -q:v 2 \"{dst}\"");
    }

    // Small, downscaled frame — for feeding to Codex vision (faster upload).
    public static async Task ExtractSmallFrameAsync(string src, string dst,
                                                    double ratio, int width = 480)
    {
        double dur = GetDuration(src);
        double t   = Math.Max(0.5, dur * ratio);
        string ts  = TimeSpan.FromSeconds(t).ToString(@"hh\:mm\:ss\.fff");
        await RunAsync($"-y -ss {ts} -i \"{src}\" -vframes 1 -vf scale={width}:-1 -q:v 4 \"{dst}\"");
    }

    // ── Compose 30/70 final video ─────────────────────────────────────────────
    // reactionVideo : webcam .avi (video only)
    // reactionAudio : mic .wav (your voice) — may be null
    // clip          : source short (video + audio)
    // textOverlay   : transparent 1080x1920 PNG with the hook text — may be null
    public static async Task ComposeAsync(string reactionVideo, string? reactionAudio,
                                          string clip, string? textOverlay, string output,
                                          IProgress<double>? pctProgress = null,
                                          double micVolume = 2.0,
                                          string? subtitlePath = null)
    {
        const int W = 1080, RH = 576, CH = 1344;

        double totalSecs = pctProgress != null ? GetDuration(clip) : 0;

        // Build input list, tracking indices
        var inputs = new List<string> { $"-i \"{reactionVideo}\"", $"-i \"{clip}\"" };
        int reactVid = 0, clipIdx = 1, next = 2;
        int overlayIdx = -1, micIdx = -1;
        if (textOverlay != null && File.Exists(textOverlay))
        { inputs.Add($"-i \"{textOverlay}\""); overlayIdx = next++; }
        // Only use the mic if it actually captured audio — an empty WAV (header only,
        // ~44 bytes) makes amix produce no samples and the AAC encoder fails (-22).
        if (reactionAudio != null && File.Exists(reactionAudio) &&
            new FileInfo(reactionAudio).Length > 1024)
        { inputs.Add($"-i \"{reactionAudio}\""); micIdx = next++; }

        bool subs = subtitlePath != null && File.Exists(subtitlePath);
        string vEnd = subs ? "vsub" : "outv";   // where the stack/overlay video ends up

        // Video: stack 30/70, then overlay hook text if present
        var fc = new System.Text.StringBuilder();
        fc.Append($"[{reactVid}:v]scale={W}:{RH}:force_original_aspect_ratio=increase,crop={W}:{RH}[top];");
        fc.Append($"[{clipIdx}:v]scale={W}:{CH}:force_original_aspect_ratio=increase,crop={W}:{CH}[bot];");
        if (overlayIdx >= 0)
        {
            fc.Append("[top][bot]vstack=inputs=2[stack];");
            fc.Append($"[stack][{overlayIdx}:v]overlay=0:0[{vEnd}]");
        }
        else
        {
            fc.Append($"[top][bot]vstack=inputs=2[{vEnd}]");
        }

        // Burn voice captions last so they sit on top of everything.
        // Use a relative filename + WorkingDirectory to avoid Windows path-escaping in the graph.
        string? subDir = null, subFile = null;
        if (subs)
        {
            subDir  = Path.GetDirectoryName(subtitlePath)!;
            subFile = Path.GetFileName(subtitlePath);
            fc.Append($";[vsub]ass={subFile}[outv]");
        }

        // Audio: mix your voice with the clip audio, or just the clip
        string audioMap;
        if (micIdx >= 0)
        {
            string mv = micVolume.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture);
            fc.Append($";[{micIdx}:a]volume={mv}[mic];[{clipIdx}:a]volume=0.85[cla];");
            fc.Append("[mic][cla]amix=inputs=2:duration=shortest:dropout_transition=2[outa]");
            audioMap = "-map [outa]";
        }
        else
        {
            audioMap = $"-map {clipIdx}:a";
        }

        string args = $"-y {string.Join(" ", inputs)} " +
                      $"-filter_complex \"{fc}\" " +
                      $"-map [outv] {audioMap} " +
                      "-c:v libx264 -preset fast -crf 22 -c:a aac -shortest " +
                      $"-progress pipe:1 -nostats \"{output}\"";

        var psi = new ProcessStartInfo(Exe)
        {
            Arguments              = args,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };
        if (subDir != null) psi.WorkingDirectory = subDir;   // so ass=<relative file> resolves

        using var p = Process.Start(psi)!;

        // Drain stderr async so it never blocks the process
        var stderrTask = p.StandardError.ReadToEndAsync();

        // Parse progress lines from stdout
        if (pctProgress != null && totalSecs > 0)
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync()) != null)
            {
                if (line.StartsWith("out_time_us=") &&
                    long.TryParse(line["out_time_us=".Length..], out long us) && us > 0)
                {
                    pctProgress.Report(Math.Clamp(us / (totalSecs * 1_000_000.0), 0.0, 1.0));
                }
                else if (line == "progress=end")
                {
                    pctProgress.Report(1.0);
                }
            }
        }
        else
        {
            await p.StandardOutput.ReadToEndAsync();
        }

        await p.WaitForExitAsync();
        string stderr = await stderrTask;
        if (p.ExitCode != 0)
        {
            // Save the FULL command + stderr so failures are diagnosable.
            try
            {
                string log = Path.Combine(Path.GetTempPath(), "ReactionStudio", "compose_debug.log");
                File.WriteAllText(log,
                    $"[{DateTime.Now:HH:mm:ss}] exit={p.ExitCode}\nARGS:\n{args}\n\nSTDERR:\n{stderr}");
            }
            catch { }

            // Surface the most relevant error line (the real cause), not just the tail.
            string reason = ExtractFfmpegError(stderr);
            throw new Exception($"ffmpeg error: {reason}");
        }
    }

    // Pull the meaningful error line out of ffmpeg's verbose stderr.
    private static string ExtractFfmpegError(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string l = lines[i].Trim();
            if (l.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("Invalid", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("No such", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("could not", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("Conversion failed", StringComparison.OrdinalIgnoreCase))
                return l;
        }
        return stderr[^Math.Min(300, stderr.Length)..];
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static string Run(string args)
    {
        var psi = new ProcessStartInfo(Exe)
        {
            Arguments              = args,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return err;
    }

    private static async Task RunAsync(string args)
    {
        var psi = new ProcessStartInfo(Exe)
        {
            Arguments              = args,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };
        using var p = Process.Start(psi)!;

        // Drain BOTH streams concurrently — otherwise a full pipe buffer
        // deadlocks ffmpeg (it blocks writing, we block waiting for exit).
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        await stdoutTask;
        string err = await stderrTask;

        if (p.ExitCode != 0)
            throw new Exception($"ffmpeg error: {err[^Math.Min(400, err.Length)..]}");
    }
}
