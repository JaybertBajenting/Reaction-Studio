using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using ReactionStudio.Services;

namespace ReactionStudio;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => { ClipList.ItemsSource = _items; RefreshClips(); };
    }

    // Live collection — adding a clip doesn't rebuild the list, so in-progress
    // script rings on other rows keep updating.
    private readonly ObservableCollection<ClipItem> _items = new();
    private int _activeDownloads = 0;

    // ── Clip list ─────────────────────────────────────────────────────────────
    private void RefreshClips()
    {
        _items.Clear();
        foreach (var f in YtDlpService.GetClips()) _items.Add(new ClipItem(f));
        UpdateIdleStatus();
    }

    private void UpdateIdleStatus()
    {
        if (_activeDownloads > 0)
        {
            StatusText.Text = $"⬇ Downloading {_activeDownloads} video(s)…";
            return;
        }
        int count = _items.Count;
        StatusText.Text = count == 0
            ? "No clips yet — paste a URL above to download one"
            : $"{count} clip(s) ready · double-click or press React";
    }

    private ClipItem? FindItem(string clipPath) =>
        _items.FirstOrDefault(i => i.Path == clipPath);

    // Generate the reaction script in the background, showing a progress ring.
    // (codex exec is opaque, so the % is a time-based estimate that ramps to ~92%.)
    private async Task GenerateScriptFor(string clipPath)
    {
        var item = FindItem(clipPath);
        if (item == null) return;

        item.IsGenerating = true;
        item.Progress     = 0;

        var sw    = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer
        { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
            item.Progress = Math.Min(92, sw.Elapsed.TotalSeconds / 32.0 * 92);
        timer.Start();

        try
        {
            string script = await ScriptService.GenerateAsync(clipPath);
            timer.Stop();
            item.Progress      = 100;
            item.IsGenerating  = false;
            item.ScriptStatus  = string.IsNullOrWhiteSpace(script) ? "⚠ failed" : "✓ Ready";
        }
        catch
        {
            timer.Stop();
            item.IsGenerating = false;
            item.ScriptStatus = "⚠ failed";
        }
    }

    private ClipItem? SelectedClip =>
        ClipList.SelectedItem as ClipItem;

    // ── Download ──────────────────────────────────────────────────────────────
    private async void Download_Click(object s, RoutedEventArgs e) => await StartDownload();
    private async void UrlBox_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await StartDownload();
    }

    // Multiple downloads can run at once. The box clears immediately so you can
    // paste the next URL right away; each finished download adds its own clip and
    // kicks off that clip's script generation.
    private async Task StartDownload()
    {
        string url = UrlBox.Text.Trim();
        if (string.IsNullOrEmpty(url)) return;
        UrlBox.Clear();

        // snapshot existing clips so we can identify what THIS download produced
        var before = new HashSet<string>(
            YtDlpService.GetClips().Select(f => f.FullName), StringComparer.OrdinalIgnoreCase);

        _activeDownloads++;
        UpdateIdleStatus();
        try
        {
            await YtDlpService.DownloadAsync(url, new Progress<string>(_ => { }));

            foreach (var f in YtDlpService.GetClips())
            {
                if (!before.Contains(f.FullName) && _items.All(i => i.Path != f.FullName))
                {
                    _items.Insert(0, new ClipItem(f));
                    _ = GenerateScriptFor(f.FullName);
                }
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Download error: {ex.Message}";
        }
        finally
        {
            _activeDownloads--;
            UpdateIdleStatus();
        }
    }

    // ── Regenerate script (per row) ──────────────────────────────────────────
    private async void RowRegen_Click(object s, RoutedEventArgs e)
    {
        if (s is FrameworkElement fe && fe.DataContext is ClipItem item && !item.IsGenerating)
            await GenerateScriptFor(item.Path);
    }

    // ── React ─────────────────────────────────────────────────────────────────
    private void React_Click(object s, RoutedEventArgs e) => OpenRecordWindow();
    private void ClipList_DoubleClick(object s, MouseButtonEventArgs e) => OpenRecordWindow();

    private void OpenRecordWindow()
    {
        var clip = SelectedClip;
        if (clip == null)
        {
            MessageBox.Show("Select a clip first.", "No clip selected",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var win = new RecordWindow(clip.Path);
        win.Owner = this;
        win.ReactionDone += OnReactionDone;
        win.ShowDialog();
    }

    private void OnReactionDone(string reactionPath, string clipPath, string? micPath)
    {
        var win = new PostWindow(reactionPath, clipPath, micPath);
        win.Owner = this;
        win.Show();
    }

    // ── Actions ───────────────────────────────────────────────────────────────
    private void Delete_Click(object s, RoutedEventArgs e)
    {
        var clip = SelectedClip;
        if (clip == null) return;
        if (MessageBox.Show($"Delete {clip.Name}?", "Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            File.Delete(clip.Path);
            try { File.Delete(ScriptService.ScriptPathFor(clip.Path)); } catch { }
            RefreshClips();
        }
    }

    private void OpenFolder_Click(object s, RoutedEventArgs e)
    {
        Directory.CreateDirectory(YtDlpService.ClipsDir);
        Process.Start("explorer.exe", YtDlpService.ClipsDir);
    }

    private void Refresh_Click(object s, RoutedEventArgs e) => RefreshClips();

    private void Settings_Click(object s, RoutedEventArgs e)
    {
        new SettingsWindow { Owner = this }.ShowDialog();
    }
}

// ── View model for clip list ──────────────────────────────────────────────────
public class ClipItem : INotifyPropertyChanged
{
    public FileInfo Info { get; }

    public ClipItem(FileInfo info)
    {
        Info = info;
        _scriptStatus = ScriptService.HasScript(info.FullName) ? "✓ Ready" : "—";
    }

    public string Path     => Info.FullName;
    public string Name     => Info.Name;
    public string Size     => $"{Info.Length / 1_000_000.0:F0} MB";
    public string Duration => GetDuration();

    private string _scriptStatus;
    public string ScriptStatus
    {
        get => _scriptStatus;
        set { _scriptStatus = value; OnPropertyChanged(); }
    }

    private bool _isGenerating;
    public bool IsGenerating
    {
        get => _isGenerating;
        set { _isGenerating = value; OnPropertyChanged(); OnPropertyChanged(nameof(NotGenerating)); }
    }

    public bool NotGenerating => !_isGenerating;

    private double _progress;
    public double Progress
    {
        get => _progress;
        set { _progress = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProgressText)); }
    }

    public string ProgressText => $"{(int)_progress}";

    private string GetDuration()
    {
        double s = FfmpegService.GetDuration(Info.FullName);
        return s > 0 ? $"{(int)s / 60}:{(int)s % 60:D2}" : "?";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
