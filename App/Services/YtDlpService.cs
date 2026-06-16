using System.Diagnostics;
using System.IO;
using System.Text;

namespace ReactionStudio.Services;

public static class YtDlpService
{
    private const string Python = @"C:\Users\M4tth3w\AppData\Local\Programs\Python\Python313\python.exe";

    public static string ClipsDir => AppPaths.Clips;

    public static async Task DownloadAsync(string url, IProgress<string> progress)
    {
        Directory.CreateDirectory(ClipsDir);
        string outtmpl = Path.Combine(ClipsDir, "%(title).60s.%(ext)s");

        string args = $"-m yt_dlp " +
                      $"--format \"bestvideo[ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best\" " +
                      $"--merge-output-format mp4 " +
                      $"--output \"{outtmpl}\" " +
                      $"--no-playlist " +
                      $"\"{url}\"";

        var psi = new ProcessStartInfo(Python)
        {
            Arguments              = args,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8
        };

        using var proc = Process.Start(psi) ?? throw new Exception("Python not found");

        // Stream output for progress
        var readTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync()) != null)
            {
                if (line.Contains("[download]") || line.Contains("[ffmpeg]"))
                    progress.Report(line.Trim());
            }
        });

        await proc.WaitForExitAsync();
        await readTask;

        if (proc.ExitCode != 0)
            throw new Exception("yt-dlp download failed.");
    }

    public static IEnumerable<FileInfo> GetClips() =>
        Directory.Exists(ClipsDir)
            ? new DirectoryInfo(ClipsDir).GetFiles("*.mp4")
                                         .OrderByDescending(f => f.LastWriteTime)
            : [];
}
