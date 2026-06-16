using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.Wave;
using OpenCvSharp;
using ReactionStudio.Models;
using ReactionStudio.Services;
using Window = System.Windows.Window;

namespace ReactionStudio;

public partial class RecordWindow : Window
{
    // reactionVideo, clipPath, micWavPath (null if mic failed)
    public event Action<string, string, string?>? ReactionDone;

    private readonly string _clipPath;
    private readonly string _tempDir;

    private VideoCapture? _cam;
    private VideoWriter?  _writer;

    // Microphone capture
    private WaveInEvent?    _mic;
    private WaveFileWriter? _micWriter;
    private string?         _micPath;

    private WriteableBitmap? _camBitmap;
    private byte[]? _latestCam;
    private int _camW, _camH;
    private readonly object _camLock = new();

    private bool _recording;
    private bool _running;
    private DateTime _recStart;
    private string? _reactionPath;

    private TeleprompterWindow? _teleprompter;
    private string _scriptText = "✨ Generating your script…";

    private readonly DispatcherTimer _displayTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };

    public RecordWindow(string clipPath)
    {
        InitializeComponent();
        _clipPath = clipPath;
        _tempDir  = Path.Combine(Path.GetTempPath(), "ReactionStudio");
        Directory.CreateDirectory(_tempDir);

        string title = Path.GetFileNameWithoutExtension(clipPath);
        TitleText.Text = $"React — {title[..Math.Min(50, title.Length)]}";

        _displayTimer.Tick += DisplayTimer_Tick;
        _running = true;

        CaptionToggle.IsChecked = AppConfig.Load().IncludeCaption;

        Loaded += (_, _) => OpenTeleprompter();
        Task.Run(PreparePreview);
    }

    // Opens the script window beside this one (drag it to another monitor).
    private void OpenTeleprompter()
    {
        _teleprompter = new TeleprompterWindow { Owner = this };
        _teleprompter.Left = Left + Width + 8;
        _teleprompter.Top  = Top;
        _teleprompter.Show();
        _teleprompter.SetScript(_scriptText);   // apply whatever we have so far
    }

    // ── Preparation ───────────────────────────────────────────────────────────
    private void PreparePreview()
    {
        SetStatus("Opening camera…");
        OpenCamera();

        Dispatcher.Invoke(() =>
        {
            // Load clip into MediaElement — plays at correct speed natively
            ClipPlayer.Source = new Uri(_clipPath);
            ClipPlayer.Play();

            PrepText.Visibility = Visibility.Collapsed;
            RecordBtn.IsEnabled = true;
            SetStatus("Ready — press Record when you're set");
            _displayTimer.Start();
        });

        // Generate the teleprompter script in the background (doesn't block recording)
        _ = GenerateScriptAsync();
    }

    // ── Teleprompter script ─────────────────────────────────────────────────
    // Uses the cached script made at download time; generates on the fly only
    // if one doesn't exist yet.
    private async Task GenerateScriptAsync()
    {
        try
        {
            if (ScriptService.HasScript(_clipPath))
            {
                SetScript(ScriptService.LoadScript(_clipPath));
                return;
            }

            SetScript("✨ Generating your script with Codex…");
            string script = await ScriptService.GenerateAsync(_clipPath);
            SetScript(string.IsNullOrWhiteSpace(script)
                ? "(Couldn't generate a script — just vibe with it!)"
                : script);
        }
        catch
        {
            SetScript("(Script generation failed)");
        }
    }

    private void SetScript(string text)
    {
        _scriptText = text;
        _teleprompter?.SetScript(text);
    }

    private void OpenCamera()
    {
        int idx = AppConfig.Load().CamIndex;
        _cam = new VideoCapture(idx);
        if (_cam.IsOpened())
        {
            _cam.Set(VideoCaptureProperties.FrameWidth,  1280);
            _cam.Set(VideoCaptureProperties.FrameHeight, 720);
            _cam.Set(VideoCaptureProperties.Fps, 30);
        }
        Task.Run(CameraLoop);
    }

    private void CameraLoop()
    {
        using var mat = new Mat();
        while (_running)
        {
            if (_cam?.Read(mat) == true && !mat.Empty())
            {
                if (_recording && _writer != null)
                    _writer.Write(mat);

                int sz  = mat.Width * mat.Height * 3;
                var buf = new byte[sz];
                Marshal.Copy(mat.Data, buf, 0, sz);
                lock (_camLock) { _latestCam = buf; _camW = mat.Width; _camH = mat.Height; }
            }
            Thread.Sleep(33);
        }
    }

    // ── MediaElement events ───────────────────────────────────────────────────
    private void ClipPlayer_MediaOpened(object s, RoutedEventArgs e)
    {
        PrepText.Visibility = Visibility.Collapsed;
    }

    private void ClipPlayer_MediaEnded(object s, RoutedEventArgs e)
    {
        // Loop the clip preview when not recording; stop if recording finished
        if (!_recording)
        {
            ClipPlayer.Position = TimeSpan.Zero;
            ClipPlayer.Play();
        }
    }

    // ── Display timer (webcam only — clip handled by MediaElement) ────────────
    private void DisplayTimer_Tick(object? s, EventArgs e)
    {
        byte[]? camFrame; int cw, ch;
        lock (_camLock) { camFrame = _latestCam; cw = _camW; ch = _camH; }
        if (camFrame != null && cw > 0)
            UpdateBitmap(ref _camBitmap, CamImage, camFrame, cw, ch);

        if (_recording)
        {
            var elapsed = DateTime.Now - _recStart;
            TimerText.Text = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}";
        }
    }

    private static void UpdateBitmap(ref WriteableBitmap? bmp, System.Windows.Controls.Image img,
                                     byte[] data, int w, int h)
    {
        if (bmp == null || bmp.PixelWidth != w || bmp.PixelHeight != h)
        {
            bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr24, null);
            img.Source = bmp;
        }
        bmp.WritePixels(new Int32Rect(0, 0, w, h), data, w * 3, 0);
    }

    // ── Record / Stop ─────────────────────────────────────────────────────────
    private void RecordBtn_Click(object s, RoutedEventArgs e)
    {
        if (!_recording) StartRecording();
        else             StopRecording();
    }

    private void StartRecording()
    {
        _recording = true;
        _recStart  = DateTime.Now;

        // Lock in the caption choice now so post-processing uses it (no re-render needed)
        var cfg = AppConfig.Load();
        cfg.IncludeCaption = CaptionToggle.IsChecked == true;
        cfg.Save();

        RecordBtn.Content    = "⏹  Stop";
        RecordBtn.Background = new SolidColorBrush(Color.FromRgb(0x1D, 0x4E, 0xD8));
        RecBadge.Visibility  = Visibility.Visible;
        SetStatus("Recording…");

        // Restart clip from beginning so it's in sync with recording start
        ClipPlayer.Position = TimeSpan.Zero;
        ClipPlayer.Play();

        // Open webcam writer
        string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _reactionPath = Path.Combine(_tempDir, $"webcam_{ts}.avi");
        int cw = (int)(_cam?.Get(VideoCaptureProperties.FrameWidth)  ?? 1280);
        int ch = (int)(_cam?.Get(VideoCaptureProperties.FrameHeight) ?? 720);
        _writer = new VideoWriter(_reactionPath,
                                   VideoWriter.FourCC('X', 'V', 'I', 'D'),
                                   30, new OpenCvSharp.Size(cw, ch));

        // Capture your voice from the default microphone
        try
        {
            _micPath = Path.Combine(_tempDir, $"mic_{ts}.wav");
            _mic = new WaveInEvent
            {
                DeviceNumber = AppConfig.Load().MicIndex,
                WaveFormat   = new WaveFormat(44100, 16, 1)
            };
            _micWriter = new WaveFileWriter(_micPath, _mic.WaveFormat);
            _mic.DataAvailable += (_, a) => _micWriter?.Write(a.Buffer, 0, a.BytesRecorded);
            _mic.StartRecording();
        }
        catch
        {
            _micPath = null;   // no mic → compose will fall back to clip audio only
        }
    }

    private void StopRecording()
    {
        _recording = false;
        ClipPlayer.Stop();

        RecordBtn.IsEnabled = false;
        RecordBtn.Content   = "⏳ Processing…";
        RecBadge.Visibility = Visibility.Collapsed;
        SetStatus("Wrapping up…");

        // Stop & flush mic
        try { _mic?.StopRecording(); } catch { }
        _mic?.Dispose();      _mic = null;
        _micWriter?.Dispose(); _micWriter = null;

        _writer?.Release();
        _writer = null;

        Task.Delay(500).ContinueWith(_ => Dispatcher.Invoke(Finish));
    }

    private void Finish()
    {
        string? rp  = _reactionPath;
        string? mic = (_micPath != null && File.Exists(_micPath)) ? _micPath : null;
        Cleanup();
        Close();
        if (rp != null && File.Exists(rp))
            ReactionDone?.Invoke(rp, _clipPath, mic);
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────
    private void Cleanup()
    {
        _running = false;
        _displayTimer.Stop();
        Dispatcher.Invoke(() =>
        {
            ClipPlayer.Stop(); ClipPlayer.Source = null;
            try { _teleprompter?.Close(); } catch { }
            _teleprompter = null;
        });
        try { _mic?.StopRecording(); } catch { }
        _mic?.Dispose();
        _micWriter?.Dispose();
        _cam?.Release();
        _writer?.Release();
    }

    private void Cancel_Click(object s, RoutedEventArgs e) { Cleanup(); Close(); }
    private void Window_Closing(object? s, System.ComponentModel.CancelEventArgs e) => Cleanup();

    private void SetStatus(string msg) =>
        Dispatcher.Invoke(() => StatusText.Text = msg);
}
