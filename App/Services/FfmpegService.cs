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

        // Build input list, tracking indices. A still image (commentary mode's
        // character) is looped so it fills the clip's duration; -shortest ends it.
        string ext = Path.GetExtension(reactionVideo).ToLowerInvariant();
        bool topIsImage = ext is ".png" or ".jpg" or ".jpeg" or ".webp";
        string reactInput = topIsImage
            ? $"-loop 1 -framerate 30 -i \"{reactionVideo}\""
            : $"-i \"{reactionVideo}\"";
        var inputs = new List<string> { reactInput, $"-i \"{clip}\"" };
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

    // ── Commentary compose: clip first, THEN the character reaction at the end ─
    // Segment A = the clip full-screen (its own audio, optional hook caption).
    // Segment B = the character reacting at the end, full-screen, with a ZOOM-IN
    //   punch ("zoom cut") + voiceover + captions. The two are concatenated.
    public static async Task ComposeCommentaryAsync(
        string clip, string characterImg, string voiceAudio, string? subtitlePath,
        string output, string workDir, double micVolume = 1.6, string? hookOverlay = null)
    {
        const int W = 1080, H = 1920;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string fill = $"scale={W}:{H}:force_original_aspect_ratio=increase,crop={W}:{H},setsar=1";
        string segA = Path.Combine(workDir, "seg_clip.mp4");
        string segB = Path.Combine(workDir, "seg_char.mp4");
        string segBblur = Path.Combine(workDir, "seg_char_blur.mp4");  // silent whip-zoom

        // Segment A — clip full-screen (+ optional hook caption), keep its audio.
        string aInputs = $"-i \"{clip}\"";
        string aFilter = $"[0:v]{fill}[v]";
        if (hookOverlay != null && File.Exists(hookOverlay))
        {
            aInputs = $"-i \"{clip}\" -i \"{hookOverlay}\"";
            aFilter = $"[0:v]{fill}[bg];[bg][1:v]overlay=0:0[v]";
        }
        await RunAsync(
            $"-y {aInputs} -filter_complex \"{aFilter}\" -map [v] -map 0:a? " +
            "-c:v libx264 -preset veryfast -crf 22 -r 30 -c:a aac -ar 44100 -ac 2 -shortest " +
            $"\"{segA}\"");

        // Segment B — character whip-zoom: a SILENT, real-radial-blur whip-zoom video
        // (BuildWhipZoomBlurredAsync) with captions + voice + whooshes added on top.
        double dv = GetDuration(voiceAudio); if (dv <= 0) dv = 6;
        int total = (int)(dv * 30) + 30;
        await BuildWhipZoomBlurredAsync(characterImg, segBblur, workDir, total);

        // Burn captions on the blurred video, mix voice + the two whooshes.
        bool subs = subtitlePath != null && File.Exists(subtitlePath);
        string bVideo = subs
            ? $"[0:v]ass={Path.GetFileName(subtitlePath)}[v]"
            : "[0:v]null[v]";
        string mv = micVolume.ToString("0.0#", inv);
        // one whoosh, duplicated and delayed to land on each of the two zooms
        // (zoom 1 ≈ frame 10 = 0.33s, zoom 2 ≈ frame 30 = 1.0s)
        string whoosh =
            "[2:a]highpass=f=200,lowpass=f=8000," +
            "volume='min(t/0.05,max((0.32-t)/0.28,0))':eval=frame,volume=2.6[whb];" +
            "[whb]asplit=2[w1][w2];[w1]adelay=333|333[wd1];[w2]adelay=1000|1000[wd2]";
        await RunAsync(
            $"-y -i \"{segBblur}\" -i \"{voiceAudio}\" " +
            "-f lavfi -t 0.4 -i \"anoisesrc=c=pink:a=0.9:d=0.4\" " +
            $"-filter_complex \"{bVideo};{whoosh};[1:a]volume={mv}[vv];" +
            "[vv][wd1][wd2]amix=inputs=3:duration=first:dropout_transition=0[a]\" " +
            "-map [v] -map [a] -c:v libx264 -preset veryfast -crf 22 -r 30 -c:a aac -ar 44100 -ac 2 -shortest " +
            $"\"{segB}\"",
            subs ? Path.GetDirectoryName(subtitlePath) : null);

        // Hard cut from clip → reaction (concat, re-encoded so params match).
        await RunAsync(
            $"-y -i \"{segA}\" -i \"{segB}\" " +
            "-filter_complex \"[0:v][0:a][1:v][1:a]concat=n=2:v=1:a=1[v][a]\" " +
            "-map [v] -map [a] -c:v libx264 -preset fast -crf 22 -c:a aac " +
            $"\"{output}\"");

        try { File.Delete(segA); File.Delete(segB); File.Delete(segBblur); } catch { }
    }

    // ── Overlay commentary compose (TRANSFORMATIVE) ───────────────────────────
    // Clip plays FULL-SCREEN for its whole duration with the character's voiceover
    // running continuously OVER it, captions throughout, and the character whip-zooms
    // in at the END as the sign-off — so the creator's voice/edit is the main event,
    // not a re-upload. Transformativeness controls:
    //   #1 audio  — clip audio DUCKED (duckVolume) or fully muted (muteOriginal),
    //               optional royalty-free music bed under the voice (musicBed).
    //   #2 reframe — dynamic push-in + periodic punch on the clip (your edit, not a replay).
    //   #3 cutaways — N brief full-frame character reaction shots overlaid mid-clip
    //               (overlaid, NOT inserted, so the voice/captions stay in sync).
    public static async Task ComposeOverlayCommentaryAsync(
        string clip, string characterImg, string voiceAudio, string? subtitlePath,
        string output, string workDir, double micVolume = 1.6, string? hookOverlay = null,
        double duckVolume = 0.10, bool muteOriginal = false,
        string? musicBed = null, double musicVolume = 0.12,
        bool reframeClip = true, int cutaways = 2,
        string? whooshSfx = null, string? impactSfx = null)
    {
        const int W = 1080, H = 1920;
        var inv  = System.Globalization.CultureInfo.InvariantCulture;
        string fill = $"scale={W}:{H}:force_original_aspect_ratio=increase,crop={W}:{H},setsar=1";
        string D(double x) => x.ToString("0.###", inv);

        double clipDur = GetDuration(clip);       if (clipDur <= 0) clipDur = 6;
        double voDur   = GetDuration(voiceAudio);  if (voDur   <= 0) voDur   = clipDur;
        // The character holds for whatever voiceover spills past the clip (+a beat),
        // clamped so the sign-off is always a sensible 2.5–6s.
        double signoffSecs   = Math.Clamp(voDur - clipDur + 1.0, 2.5, 6.0);
        int    signoffFrames = (int)(signoffSecs * 30);
        double totalDur      = clipDur + signoffSecs;            // exact final length
        string tdur          = D(totalDur);

        // #3 Mid-clip full-frame character cutaways (skip on very short clips).
        const double cutDur = 1.0;
        int nCut = clipDur >= 5 ? Math.Max(0, cutaways) : 0;
        var cutTimes = new List<double>();
        for (int i = 0; i < nCut; i++) cutTimes.Add(clipDur * (i + 1) / (nCut + 1));

        // Silent whip-zoom clips: the end sign-off + (if used) one reusable cutaway.
        string charBlur = Path.Combine(workDir, "seg_signoff_blur.mp4");
        await BuildWhipZoomBlurredAsync(characterImg, charBlur, workDir, signoffFrames);
        string cutBlur = Path.Combine(workDir, "seg_cut_blur.mp4");
        if (nCut > 0) await BuildWhipZoomBlurredAsync(characterImg, cutBlur, workDir, (int)(cutDur * 30));

        bool   subs      = subtitlePath != null && File.Exists(subtitlePath);
        bool   hook      = hookOverlay != null && File.Exists(hookOverlay);
        bool   music     = !string.IsNullOrWhiteSpace(musicBed) && File.Exists(musicBed);
        bool   clipAudio = !muteOriginal && HasAudio(clip);     // #1 mute option
        string mv   = micVolume.ToString("0.0#", inv);
        string duck = duckVolume.ToString("0.0#", inv);
        string musv = musicVolume.ToString("0.0#", inv);

        // ── inputs (index order matters; -t caps output so looped/lavfi inputs are safe) ──
        // #SFX whoosh source: a real sound from the user's SFX library if supplied,
        // otherwise the synthesized pink-noise whoosh.
        bool realWhoosh = !string.IsNullOrWhiteSpace(whooshSfx) && File.Exists(whooshSfx);
        var ins = new List<string>
        {
            $"-i \"{clip}\"",                                       // 0 clip
            $"-i \"{charBlur}\"",                                   // 1 signoff char
            $"-i \"{voiceAudio}\"",                                 // 2 voice
            realWhoosh ? $"-i \"{whooshSfx}\""                      // 3 whoosh source
                       : "-f lavfi -t 0.4 -i \"anoisesrc=c=pink:a=0.9:d=0.4\"",
        };
        bool realImpact = !string.IsNullOrWhiteSpace(impactSfx) && File.Exists(impactSfx);
        int n = 4;
        int iHook   = -1; if (hook)      { iHook   = n++; ins.Add($"-i \"{hookOverlay}\""); }
        int iCut    = -1; if (nCut > 0)  { iCut    = n++; ins.Add($"-i \"{cutBlur}\""); }
        int iMusic  = -1; if (music)     { iMusic  = n++; ins.Add($"-stream_loop -1 -i \"{musicBed}\""); }
        int iImpact = -1; if (realImpact){ iImpact = n++; ins.Add($"-i \"{impactSfx}\""); }

        // ── VIDEO graph ──
        var v = new System.Text.StringBuilder();
        // #2 Reframe: gentle continuous push-in + a quick punch every ~3.5s (105f).
        string reframe = "";
        if (reframeClip)
        {
            int clipFrames = Math.Max(30, (int)(clipDur * 30));
            string drift = $"min(0.02+{D(0.04)}*on/{clipFrames},0.06)";
            string ph    = "(on-105*trunc(on/105))";
            string punch = $"if(lt({ph},4),0.10*{ph}/4,if(lt({ph},16),0.10*(16-{ph})/12,0))";
            reframe = $",zoompan=z='1+{drift}+{punch}':d=1:" +
                      $"x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':s={W}x{H}:fps=30";
        }
        v.Append($"[0:v]{fill}{reframe}[cf0];");
        string cur = "cf0";
        // hook overlay keeps its alpha (no format=yuv420p before the overlay).
        if (hook) { v.Append($"[{iHook}:v]setsar=1[hk];[{cur}][hk]overlay=0:0[cfh];"); cur = "cfh"; }
        // cutaways: split the reusable whip, time-shift each, overlay only in its window.
        if (nCut > 0)
        {
            v.Append($"[{iCut}:v]fps=30,setsar=1,format=yuv420p,split={nCut}");
            for (int i = 0; i < nCut; i++) v.Append($"[cs{i}]");
            v.Append(";");
            for (int i = 0; i < nCut; i++)
            {
                double t0 = cutTimes[i], t1 = t0 + cutDur;
                v.Append($"[cs{i}]tpad=start_duration={D(t0)}[ct{i}];");
                v.Append($"[{cur}][ct{i}]overlay=0:0:enable='between(t,{D(t0)},{D(t1)})'[cc{i}];");
                cur = $"cc{i}";
            }
        }
        v.Append($"[{cur}]fps=30,setsar=1,format=yuv420p[clipv];");
        v.Append("[1:v]fps=30,setsar=1,format=yuv420p[charv];");
        v.Append("[clipv][charv]concat=n=2:v=1:a=0[vc];");
        v.Append(subs ? $"[vc]ass={Path.GetFileName(subtitlePath)}[v]" : "[vc]null[v]");

        // ── AUDIO graph (#1) ── whoosh hits: 2 on the signoff zooms + one per cutaway.
        var hits = new List<double> { clipDur + 0.33, clipDur + 1.00 };
        hits.AddRange(cutTimes);
        var aud = new System.Text.StringBuilder();
        // Real SFX needs only a gain + format normalize; the synth needs shaping into a whoosh.
        aud.Append(realWhoosh
            ? "[3:a]volume=1.2,aformat=sample_rates=44100:channel_layouts=stereo[whb];"
            : "[3:a]highpass=f=200,lowpass=f=8000," +
              "volume='min(t/0.05,max((0.32-t)/0.28,0))':eval=frame,volume=2.6[whb];");
        aud.Append($"[whb]asplit={hits.Count}");
        for (int i = 0; i < hits.Count; i++) aud.Append($"[wq{i}]");
        aud.Append(";");
        var mix = new List<string>();
        for (int i = 0; i < hits.Count; i++)
        {
            int ms = (int)Math.Round(hits[i] * 1000);
            aud.Append($"[wq{i}]adelay={ms}:all=1[wd{i}];");   // all=1 → robust to mono/stereo
            mix.Add($"[wd{i}]");
        }
        // #SFX impact: a generic punch landing ~0.15s into each whip (on the zoom's land).
        if (realImpact)
        {
            aud.Append($"[{iImpact}:a]volume=0.5,aformat=sample_rates=44100:channel_layouts=stereo[imp];");
            aud.Append($"[imp]asplit={hits.Count}");
            for (int i = 0; i < hits.Count; i++) aud.Append($"[iq{i}]");
            aud.Append(";");
            for (int i = 0; i < hits.Count; i++)
            {
                int ms = (int)Math.Round((hits[i] + 0.15) * 1000);
                aud.Append($"[iq{i}]adelay={ms}:all=1[id{i}];");
                mix.Add($"[id{i}]");
            }
        }
        if (clipAudio) { aud.Append($"[0:a]volume={duck}[ca];"); mix.Add("[ca]"); }
        aud.Append($"[2:a]volume={mv}[vo];"); mix.Add("[vo]");
        if (music) { aud.Append($"[{iMusic}:a]volume={musv},atrim=end={tdur},asetpts=PTS-STARTPTS[mus];"); mix.Add("[mus]"); }
        // normalize=0 (sum, not mean) keeps the voice loud over the ducked clip; limiter
        // guards peaks; apad is BOUNDED (never infinite); -t hard-caps the whole encode.
        aud.Append($"{string.Concat(mix)}amix=inputs={mix.Count}:duration=longest:normalize=0[am];");
        aud.Append($"[am]alimiter=limit=0.9:attack=1:release=40,apad=whole_dur={tdur}[a]");

        await RunAsync(
            $"-y {string.Join(" ", ins)} -filter_complex \"{v};{aud}\" " +
            $"-map [v] -map [a] -c:v libx264 -preset fast -crf 22 -r 30 -c:a aac -ar 44100 -ac 2 -t {tdur} " +
            $"\"{output}\"",
            subs ? Path.GetDirectoryName(subtitlePath) : null);

        try { File.Delete(charBlur); File.Delete(cutBlur); } catch { }
    }

    // Builds a SILENT, caption-less whip-zoom of the character: ffmpeg zoompan renders
    // the raw double zoom, then a REAL OpenCV radial motion blur is applied per-frame
    // with strength tracking the zoom velocity (holds stay sharp, ramps streak).
    // `frames` = total frames at 30fps. Callers layer captions/audio on top.
    private static async Task BuildWhipZoomBlurredAsync(
        string characterImg, string blurredOut, string workDir, int frames)
    {
        const int W = 1080, H = 1920;
        string fill = $"scale={W}:{H}:force_original_aspect_ratio=increase,crop={W}:{H},setsar=1";
        string raw  = Path.Combine(workDir, Path.GetFileNameWithoutExtension(blurredOut) + "_raw.mp4");
        int total   = Math.Max(30, frames);

        string z  = "if(lt(on,10),1.0,if(lt(on,17),1.0+0.25*(on-10)/7," +
                    "if(lt(on,30),1.25,if(lt(on,37),1.25+0.25*(on-30)/7,1.5))))";
        string zp = $"zoompan=z='{z}':x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':d={total}:s={W}x{H}:fps=30";

        double Zoom(int on) =>
            on < 10 ? 1.0 :
            on < 17 ? 1.0 + 0.25 * (on - 10) / 7.0 :
            on < 30 ? 1.25 :
            on < 37 ? 1.25 + 0.25 * (on - 30) / 7.0 :
                      1.5;
        const double blurGain = 12.0, blurMax = 0.30;
        double Strength(int n)
        {
            int lo = Math.Max(0, n - 1), hi = Math.Min(total - 1, n + 1);
            double vel = Math.Abs(Zoom(hi) - Zoom(lo)) / Math.Max(1, hi - lo);   // central diff
            return Math.Clamp(blurGain * vel, 0.0, blurMax);
        }

        await RunAsync(
            $"-y -loop 1 -framerate 30 -i \"{characterImg}\" " +
            $"-filter_complex \"[0:v]{fill},{zp}[v]\" -map [v] -frames:v {total} " +
            $"-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p -r 30 \"{raw}\"");

        MotionBlurService.ZoomBlurVideo(raw, blurredOut, Strength);
        try { File.Delete(raw); } catch { }
    }

    // True if the file has at least one audio stream (parsed from ffmpeg's stream dump).
    public static bool HasAudio(string path) => Run($"-i \"{path}\"").Contains("Audio:");

    // Full-screen thumbnail (clip frame filling 9:16) with the hook caption overlay.
    public static async Task BuildFullscreenThumbAsync(string clipFrame, string? hookOverlay, string outJpg)
    {
        const int W = 1080, H = 1920;
        string fill = $"scale={W}:{H}:force_original_aspect_ratio=increase,crop={W}:{H}";
        if (hookOverlay != null && File.Exists(hookOverlay))
            await RunAsync($"-y -i \"{clipFrame}\" -i \"{hookOverlay}\" " +
                           $"-filter_complex \"[0:v]{fill}[bg];[bg][1:v]overlay=0:0\" -frames:v 1 -q:v 2 \"{outJpg}\"");
        else
            await RunAsync($"-y -i \"{clipFrame}\" -vf \"{fill}\" -frames:v 1 -q:v 2 \"{outJpg}\"");
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

    private static async Task RunAsync(string args, string? workingDir = null)
    {
        var psi = new ProcessStartInfo(Exe)
        {
            Arguments              = args,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };
        if (workingDir != null) psi.WorkingDirectory = workingDir;
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
