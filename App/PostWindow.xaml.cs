using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using ReactionStudio.Services;
using Window = System.Windows.Window;

namespace ReactionStudio;

public partial class PostWindow : Window
{
    private readonly string  _reactionPath;
    private readonly string  _clipPath;
    private readonly string? _micPath;
    private string? _outputPath;
    private string? _thumbPath;

    // cached between initial render and re-renders
    private string _reactFrame = "", _clipFrame = "";
    private string _composeClip = "";   // original clip, or censored version if profanity was blurred
    private string _subtitlePath = "";  // generated .ass of your voice captions (empty if none)

    // Per-job scratch folder so concurrent PostWindows never clobber each other's
    // frames / overlay / thumbnail temp files.
    private readonly string _work;

    private static string OutDir   => AppPaths.Output;
    private static string ThumbDir => AppPaths.Thumbnails;

    public PostWindow(string reactionPath, string clipPath, string? micPath = null)
    {
        InitializeComponent();
        _reactionPath = reactionPath;
        _clipPath     = clipPath;
        _micPath      = micPath;
        _work = Path.Combine(Path.GetTempPath(), "ReactionStudio", "job_" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(OutDir);
        Directory.CreateDirectory(ThumbDir);
        Directory.CreateDirectory(_work);
        Loaded += (_, _) => _ = ProcessAsync();
    }

    private async Task ProcessAsync()
    {
        string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        _outputPath = Path.Combine(OutDir,   $"reaction_{ts}.mp4");
        _thumbPath  = Path.Combine(ThumbDir, $"thumb_{ts}.jpg");
        string tempDir = _work;
        _reactFrame  = Path.Combine(tempDir, "react_frame.jpg");
        _clipFrame   = Path.Combine(tempDir, "clip_frame.jpg");
        _composeClip = _clipPath;

        var cfg = ReactionStudio.Models.AppConfig.Load();
        bool includeCaption = cfg.IncludeCaption;
        Dispatcher.Invoke(() =>
        {
            CaptionToggle.IsChecked      = includeCaption;
            VoiceCaptionToggle.IsChecked = cfg.AutoCaptions;
        });

        // ── Step 1: Auto-blur profanity baked into the clip ──────────────────
        if (cfg.BlurProfanity)
        {
            SetProgress(3, "[1/4]  Checking clip for on-screen profanity…");
            var det = new List<string>();
            double[] ratios = { 0.2, 0.5, 0.8 };
            for (int i = 0; i < ratios.Length; i++)
            {
                string f = Path.Combine(tempDir, $"prof_{i}.jpg");
                await FfmpegService.ExtractSmallFrameAsync(_clipPath, f, ratios[i], 640);
                if (File.Exists(f)) det.Add(f);
            }

            var boxes = await CodexService.DetectProfanityBoxesAsync(det);
            if (boxes.Count > 0)
            {
                SetProgress(8, "[1/4]  Blurring profanity in clip…");
                string censored = Path.Combine(tempDir, "censored_clip.mp4");
                try
                {
                    var regions = boxes.Select(b => (b.X, b.Y, b.W, b.H)).ToList();
                    await FfmpegService.CensorRegionsAsync(_clipPath, censored, regions);
                    if (File.Exists(censored)) _composeClip = censored;
                }
                catch { /* fall back to original clip */ }
            }
            foreach (var f in det) { try { File.Delete(f); } catch { } }
        }

        // ── Step 2: Frames (from the clip we'll actually use) ────────────────
        SetProgress(12, "[2/4]  Extracting frames…");
        await FfmpegService.ExtractFrameAsync(_reactionPath, _reactFrame, 0.4);
        await FfmpegService.ExtractFrameAsync(_composeClip,  _clipFrame,  0.4);

        // ── Step 3: Caption + YouTube title via Codex ────────────────────────
        SetProgress(16, "[3/4]  Writing caption + title with Codex…");
        string clipTitle = Path.GetFileNameWithoutExtension(_clipPath);
        var (caption, title) = await CodexService.GenerateCaptionAndTitleAsync(
            clipTitle, new[] { _clipFrame });
        Dispatcher.Invoke(() => { CaptionBox.Text = caption; TitleBox.Text = title; });

        // ── Step 4: Transcribe your voice → caption file (once) ──────────────
        if (cfg.AutoCaptions && _micPath != null && File.Exists(_micPath) &&
            new FileInfo(_micPath).Length > 1024)
        {
            SetProgress(18, "[4/5]  Transcribing your voice (Whisper)…");
            try
            {
                var words = await WhisperService.TranscribeWordsAsync(_micPath);
                string ass = Path.Combine(tempDir, "captions.ass");
                if (SubtitleService.WriteAss(ass, words)) _subtitlePath = ass;
            }
            catch { /* no captions if it fails */ }
        }

        // ── Step 5: Thumbnail + overlay + compose ────────────────────────────
        await RenderAsync(caption, includeCaption);
    }

    // Builds thumbnail + (optional) text overlay, then composes the final video.
    // Re-runnable from the "Re-render" button after editing the caption/toggle.
    private async Task RenderAsync(string caption, bool includeCaption)
    {
        string overlayPng = Path.Combine(_work, "text_overlay.png");

        // When caption is off, build the thumbnail with no text and skip the overlay.
        string thumbCaption = includeCaption ? caption : "";

        SetProgress(22, "Building thumbnail…");
        await BuildThumbAndOverlayAsync(_work, _reactFrame, _clipFrame, thumbCaption, _thumbPath!, overlayPng);

        SetProgress(30, "Composing video…");
        // Release the output file if the preview is holding it (re-render case)
        Dispatcher.Invoke(() => { VideoPlayer.Stop(); VideoPlayer.Source = null; });
        try
        {
            var pct = new Progress<double>(f =>
                SetProgress(30 + f * 68, "Composing video (text + your voice)…"));

            string? overlay = (includeCaption && File.Exists(overlayPng)) ? overlayPng : null;
            double micVol = ReactionStudio.Models.AppConfig.Load().MicVolume;
            string clipForCompose = string.IsNullOrEmpty(_composeClip) ? _clipPath : _composeClip;

            bool wantVoiceCaps = Dispatcher.Invoke(() => VoiceCaptionToggle.IsChecked == true);
            string? subs = (wantVoiceCaps && !string.IsNullOrEmpty(_subtitlePath) &&
                            File.Exists(_subtitlePath)) ? _subtitlePath : null;

            await FfmpegService.ComposeAsync(_reactionPath, _micPath, clipForCompose,
                                             overlay, _outputPath!, pct, micVol, subs);
        }
        catch (Exception ex)
        {
            SetProgress(30, $"Compose error: {ex.Message[..Math.Min(80, ex.Message.Length)]}");
            return;
        }
        SetProgress(100, "Done!");

        Dispatcher.Invoke(() =>
        {
            UploadBtn.IsEnabled   = true;
            RerenderBtn.IsEnabled = true;

            if (File.Exists(_thumbPath))
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource   = new Uri(_thumbPath);
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bi.EndInit();
                ThumbImage.Source = bi;
            }

            if (File.Exists(_outputPath))
            {
                VideoPlayer.Stop();
                VideoPlayer.Source = new Uri(_outputPath);
                VideoPlayer.Play();
                _videoPlaying = true;
            }
        });
    }

    private async void Rerender_Click(object s, RoutedEventArgs e)
    {
        UploadBtn.IsEnabled   = false;
        RerenderBtn.IsEnabled = false;

        bool include = CaptionToggle.IsChecked == true;
        // Remember the preference for next time
        var cfg = ReactionStudio.Models.AppConfig.Load();
        cfg.IncludeCaption = include;
        cfg.Save();

        await RenderAsync(CaptionBox.Text.Trim(), include);
    }

    // ── Video preview controls ──────────────────────────────────────────────
    private bool _videoPlaying;

    private void VideoPlayer_MouseDown(object s, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (VideoPlayer.Source == null) return;
        if (_videoPlaying) { VideoPlayer.Pause(); PlayIcon.Visibility = Visibility.Visible; }
        else               { VideoPlayer.Play();  PlayIcon.Visibility = Visibility.Collapsed; }
        _videoPlaying = !_videoPlaying;
    }

    private void VideoPlayer_MediaEnded(object s, RoutedEventArgs e)
    {
        // Loop the preview
        VideoPlayer.Position = TimeSpan.Zero;
        VideoPlayer.Play();
    }

    private void Window_Closing(object? s, System.ComponentModel.CancelEventArgs e)
    {
        try { VideoPlayer.Stop(); VideoPlayer.Source = null; } catch { }
        try { Directory.Delete(_work, true); } catch { }
    }

    // Builds BOTH the YouTube thumbnail and a transparent text-overlay PNG for the
    // video — using identical hook-text styling (bold white + thick black outline)
    // so the on-screen text matches between the thumbnail and the video.
    private static async Task BuildThumbAndOverlayAsync(string workDir, string reactFrame, string clipFrame,
                                                        string caption, string thumbOut,
                                                        string overlayOut)
    {
        string scriptPath = Path.Combine(workDir, "make_thumb.py");
        string capFile    = Path.Combine(workDir, "caption.txt");
        await File.WriteAllTextAsync(capFile, caption);

        string script =
            "import sys\n" +
            "from PIL import Image, ImageDraw, ImageFont\n" +
            "react_f, clip_f, cap_f, thumb_out, ov_out = sys.argv[1:6]\n" +
            "W, H = 1080, 1920\n" +
            "rh, ch = int(H*0.30), int(H*0.70)\n" +
            "caption = open(cap_f, encoding='utf-8').read().strip()\n" +
            "def load_font(sz):\n" +
            "    for p in (r'C:/Windows/Fonts/arialbd.ttf', r'C:/Windows/Fonts/Arial.ttf'):\n" +
            "        try: return ImageFont.truetype(p, sz)\n" +
            "        except: pass\n" +
            "    return ImageFont.load_default()\n" +
            "def fill_crop(img, w, h):\n" +
            "    ir, tr = img.width/img.height, w/h\n" +
            "    nw, nh = (int(h*ir), h) if ir > tr else (w, int(w/ir))\n" +
            "    img = img.resize((nw, nh), Image.LANCZOS)\n" +
            "    return img.crop(((nw-w)//2, (nh-h)//2, (nw-w)//2+w, (nh-h)//2+h))\n" +
            "def wrap(draw, text, font, maxw):\n" +
            "    words, lines, line = text.split(), [], ''\n" +
            "    for w in words:\n" +
            "        t = (line+' '+w).strip()\n" +
            "        if draw.textlength(t, font=font) > maxw:\n" +
            "            if line: lines.append(line)\n" +
            "            line = w\n" +
            "        else: line = t\n" +
            "    if line: lines.append(line)\n" +
            "    return lines[:3]\n" +
            "def draw_hook(canvas):\n" +
            "    if not caption: return\n" +
            "    draw = ImageDraw.Draw(canvas)\n" +
            "    font = load_font(78)\n" +
            "    lines = wrap(draw, caption, font, W-90)\n" +
            "    lh, y = 94, rh + 26\n" +
            "    for ln in lines:\n" +
            "        tw = draw.textlength(ln, font=font); x = (W-tw)//2\n" +
            "        draw.text((x, y), ln, font=font, fill=(255,255,255),\n" +
            "                  stroke_width=9, stroke_fill=(0,0,0))\n" +
            "        y += lh\n" +
            "# Thumbnail: face + clip + hook text\n" +
            "try: top = Image.open(react_f).convert('RGB')\n" +
            "except: top = Image.new('RGB', (W, rh), (20,20,40))\n" +
            "try: bot = Image.open(clip_f).convert('RGB')\n" +
            "except: bot = Image.new('RGB', (W, ch), (10,10,20))\n" +
            "thumb = Image.new('RGB', (W, H))\n" +
            "thumb.paste(fill_crop(top, W, rh), (0, 0))\n" +
            "thumb.paste(fill_crop(bot, W, ch), (0, rh))\n" +
            "draw_hook(thumb)\n" +
            "thumb.save(thumb_out, 'JPEG', quality=92)\n" +
            "# Transparent text overlay for the video (same text, same position)\n" +
            "ov = Image.new('RGBA', (W, H), (0,0,0,0))\n" +
            "draw_hook(ov)\n" +
            "ov.save(ov_out, 'PNG')\n";

        await File.WriteAllTextAsync(scriptPath, script);

        string args = $"\"{scriptPath}\" \"{reactFrame}\" \"{clipFrame}\" \"{capFile}\" " +
                      $"\"{thumbOut}\" \"{overlayOut}\"";
        var psi = new ProcessStartInfo(
            @"C:\Users\M4tth3w\AppData\Local\Programs\Python\Python313\python.exe", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
    }

    // ── Upload ────────────────────────────────────────────────────────────────
    private async void Upload_Click(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            MessageBox.Show("Add a title first.", "No title",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var cfg = ReactionStudio.Models.AppConfig.Load();
        DateTimeOffset? slot = cfg.SchedulePublic ? ScheduleService.PeekNextSlot() : null;

        string msg = slot.HasValue
            ? $"Upload and SCHEDULE to go public automatically?\n\nTitle: {TitleBox.Text}\n\n" +
              $"Goes public: {ScheduleService.Describe(slot.Value)}\n\n" +
              "It stays private until then, so you can still review/cancel on YouTube."
            : $"Upload to your YouTube channel as PRIVATE?\n\nTitle: {TitleBox.Text}\n\n" +
              "It will not be public — you can publish it later yourself.";

        var confirm = MessageBox.Show(msg, "Confirm upload",
                                      MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        UploadBtn.IsEnabled   = false;
        RerenderBtn.IsEnabled = false;
        UploadBtn.Content     = "Uploading…";
        try
        {
            await YTUploader.UploadAsync(
                _outputPath!, _thumbPath!, TitleBox.Text,
                new Progress<string>(msg => SetStatus(msg)), slot);

            // Only now (upload succeeded) do we burn the slot, so cancels/failures don't leave gaps.
            if (slot.HasValue) ScheduleService.Commit(slot.Value);
            UploadBtn.Content = slot.HasValue ? "✅ Scheduled public" : "✅ Uploaded (private)";
        }
        catch (Exception ex)
        {
            SetStatus($"Upload error: {ex.Message}");
            UploadBtn.IsEnabled   = true;
            RerenderBtn.IsEnabled = true;
            UploadBtn.Content     = "🚀  Upload to YouTube";
        }
    }

    private void OpenOutput_Click(object s, RoutedEventArgs e) =>
        Process.Start("explorer.exe", OutDir);

    // ── Helpers ───────────────────────────────────────────────────────────────
    private void SetProgress(double pct, string label) =>
        Dispatcher.Invoke(() =>
        {
            ProgressBar.Value = pct;
            PctText.Text      = pct >= 100 ? "✓" : $"{pct:0}%";
            StatusText.Text   = label;
        });

    private void SetStatus(string msg) =>
        Dispatcher.Invoke(() => StatusText.Text = msg);
}
