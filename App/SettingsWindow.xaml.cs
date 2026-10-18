using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ReactionStudio.Models;
using ReactionStudio.Services;
using Window = System.Windows.Window;

namespace ReactionStudio;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _cfg = AppConfig.Load();
    private readonly List<CameraDevice> _cameras = [];
    private readonly List<MicDevice> _mics = [];

    private static readonly string SecretDir = Services.AppPaths.Base;

    public SettingsWindow()
    {
        InitializeComponent();
        SecretPathText.Text = Path.Combine(SecretDir, "client_secret.json");
        VolSlider.Value = _cfg.MicVolume;
        BlurProfanityToggle.IsChecked = _cfg.BlurProfanity;
        AutoCaptionsToggle.IsChecked  = _cfg.AutoCaptions;
        ScheduleToggle.IsChecked = _cfg.SchedulePublic;
        DayHourBox.Text   = _cfg.PeakHourDay.ToString();
        NightHourBox.Text = _cfg.PeakHourNight.ToString();
        ElevenKeyBox.Text = _cfg.ElevenLabsApiKey;
        VoiceIdBox.Text   = _cfg.ElevenLabsVoiceId;
        UpdateVolLabel();
        ShowStyleProfile();
        ShowCharacter();
        Loaded += (_, _) => { _ = ScanAsync(); ScanMics(); };
    }

    private void VolSlider_Changed(object s, RoutedPropertyChangedEventArgs<double> e) =>
        UpdateVolLabel();

    private void UpdateVolLabel()
    {
        if (VolLabel != null)
            VolLabel.Text = $"{(int)Math.Round(VolSlider.Value * 100)}%";
    }

    // ── Microphones ───────────────────────────────────────────────────────────
    private void ScanMics()
    {
        _mics.Clear();
        _mics.AddRange(AudioService.ListMics());

        MicCombo.Items.Clear();
        foreach (var m in _mics)
            MicCombo.Items.Add(m.Index < 0 ? m.Name : $"{m.Index}  —  {m.Name}");

        int sel = _mics.FindIndex(m => m.Index == _cfg.MicIndex);
        MicCombo.SelectedIndex = sel >= 0 ? sel : 0;
    }

    private void ScanMics_Click(object s, RoutedEventArgs e) => ScanMics();

    // ── Style reference ───────────────────────────────────────────────────────
    private void ShowStyleProfile()
    {
        var p = Services.StyleService.Load();
        StyleStatus.Text = p == null || p.Examples.Count == 0
            ? "Paste a channel URL (e.g. youtube.com/@handle) and click Learn."
            : $"✓ Learned {p.Examples.Count} from {p.Channel} on {p.LearnedAt}";
    }

    private async void Learn_Click(object s, RoutedEventArgs e)
    {
        string url = StyleUrlBox.Text.Trim();
        if (url.Length == 0)
        {
            MessageBox.Show("Paste a YouTube channel URL first.", "No URL",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        LearnBtn.IsEnabled = false;
        var prog = new Progress<string>(m => StyleStatus.Text = m);
        try
        {
            await Services.StyleService.LearnFromChannelAsync(url, 25, prog);
            ShowStyleProfile();
        }
        catch (Exception ex)
        {
            StyleStatus.Text = $"Learn failed: {ex.Message}";
        }
        LearnBtn.IsEnabled = true;
    }

    private void ClearStyle_Click(object s, RoutedEventArgs e)
    {
        Services.StyleService.Clear();
        ShowStyleProfile();
    }

    // ── AI character ──────────────────────────────────────────────────────────
    private void ShowCharacter()
    {
        bool ok = !string.IsNullOrEmpty(_cfg.CharacterImage) && File.Exists(_cfg.CharacterImage);
        CharStatus.Text = ok
            ? $"✓ Character ready: {Path.GetFileName(_cfg.CharacterImage)}"
            : "No character yet — pick your own image or generate one.";

        if (ok)
        {
            var bi = new System.Windows.Media.Imaging.BitmapImage();
            bi.BeginInit();
            bi.UriSource     = new Uri(_cfg.CharacterImage);
            bi.CacheOption   = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bi.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
            bi.EndInit();
            CharPreview.Source     = bi;
            CharPreview.Visibility = Visibility.Visible;
        }
        else CharPreview.Visibility = Visibility.Collapsed;
    }

    private void PickChar_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Pick your character image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.webp"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            Directory.CreateDirectory(Services.CharacterService.CharDir);
            string dst = Path.Combine(Services.CharacterService.CharDir,
                                      "character" + Path.GetExtension(dlg.FileName).ToLowerInvariant());
            // remove any previous character files so the picked one is unambiguous
            foreach (var old in Directory.GetFiles(Services.CharacterService.CharDir, "character.*"))
                { try { File.Delete(old); } catch { } }
            File.Copy(dlg.FileName, dst, true);
            _cfg.CharacterImage = dst;
            ShowCharacter();
        }
        catch (Exception ex)
        {
            CharStatus.Text = $"Couldn't load image: {ex.Message}";
        }
    }

    private async void GenChar_Click(object s, RoutedEventArgs e)
    {
        GenCharBtn.IsEnabled = false;
        var prog = new Progress<string>(m => CharStatus.Text = m);
        try
        {
            string png = await Services.CharacterService.GenerateAsync(CharDescBox.Text.Trim(), prog);
            _cfg.CharacterImage = png;   // keep in-memory cfg in sync so Save doesn't wipe it
            ShowCharacter();
        }
        catch (Exception ex)
        {
            CharStatus.Text = $"Generate failed: {ex.Message}";
        }
        GenCharBtn.IsEnabled = true;
    }

    private async Task ScanAsync()
    {
        CameraCombo.IsEnabled = false;
        CameraCombo.Items.Clear();
        CameraCombo.Items.Add("Scanning cameras…");
        CameraCombo.SelectedIndex = 0;

        var found = await Task.Run(CameraService.ListCameras);
        _cameras.Clear();
        _cameras.AddRange(found);

        CameraCombo.Items.Clear();
        if (_cameras.Count == 0)
        {
            CameraCombo.Items.Add("No cameras found");
            CameraCombo.SelectedIndex = 0;
        }
        else
        {
            foreach (var c in _cameras)
                CameraCombo.Items.Add($"Camera {c.Index}  —  {c.Name}");

            // Pre-select saved index
            int savedIdx = _cfg.CamIndex;
            int sel = _cameras.FindIndex(c => c.Index == savedIdx);
            CameraCombo.SelectedIndex = sel >= 0 ? sel : 0;
        }
        CameraCombo.IsEnabled = true;
    }

    private void ScanCameras_Click(object s, RoutedEventArgs e) => _ = ScanAsync();

    private void Save_Click(object s, RoutedEventArgs e)
    {
        if (CameraCombo.SelectedIndex >= 0 && CameraCombo.SelectedIndex < _cameras.Count)
            _cfg.CamIndex = _cameras[CameraCombo.SelectedIndex].Index;

        if (MicCombo.SelectedIndex >= 0 && MicCombo.SelectedIndex < _mics.Count)
            _cfg.MicIndex = _mics[MicCombo.SelectedIndex].Index;

        _cfg.MicVolume     = VolSlider.Value;
        _cfg.BlurProfanity = BlurProfanityToggle.IsChecked == true;
        _cfg.AutoCaptions  = AutoCaptionsToggle.IsChecked == true;
        _cfg.SchedulePublic = ScheduleToggle.IsChecked == true;
        _cfg.ElevenLabsApiKey  = ElevenKeyBox.Text.Trim();
        _cfg.ElevenLabsVoiceId = VoiceIdBox.Text.Trim();
        if (int.TryParse(DayHourBox.Text,   out int dh) && dh is >= 0 and <= 23) _cfg.PeakHourDay   = dh;
        if (int.TryParse(NightHourBox.Text, out int nh) && nh is >= 0 and <= 23) _cfg.PeakHourNight = nh;

        _cfg.Save();
        Close();
    }

    private void SecretPath_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
    {
        Directory.CreateDirectory(SecretDir);
        Process.Start("explorer.exe", SecretDir);
    }
}
