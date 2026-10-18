using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ReactionStudio.Models;

namespace ReactionStudio.Services;

/// <summary>
/// Generates an ORIGINAL character avatar: Codex designs it as an SVG, then a
/// headless Chromium renders it to a transparent PNG used as the on-screen reactor.
/// </summary>
public static class CharacterService
{
    private static readonly string[] Browsers =
    {
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe",
        @"C:\Program Files\Google\Chrome\Application\chrome.exe"
    };

    public static string CharDir => Path.Combine(AppPaths.Base, "character");

    public static async Task<string> GenerateAsync(string description, IProgress<string> progress)
    {
        Directory.CreateDirectory(CharDir);

        progress.Report("Asking Codex to design your character…");
        string prompt =
            "Design an ORIGINAL cartoon mascot character (do NOT copy any existing/branded character). " +
            "Output it as ONE self-contained SVG: a front-facing head-and-shoulders mascot, bold flat " +
            "colors, thick dark outlines, big expressive eyes, friendly meme-able vibe, transparent background. " +
            (description.Length > 0 ? $"Theme/look: {description}. " : "") +
            "Output ONLY the raw SVG markup starting with <svg and ending with </svg>, " +
            "width=\"600\" height=\"600\" viewBox=\"0 0 600 600\". No markdown fences, no commentary.";

        string reply = await CodexService.AskAsync(prompt);
        var m = Regex.Match(reply, @"<svg[\s\S]*?</svg>", RegexOptions.IgnoreCase);
        if (!m.Success) throw new Exception("Codex didn't return an SVG. Try a different description.");
        string svg = m.Value;

        progress.Report("Rendering character…");
        string png = Path.Combine(CharDir, "character.png");
        await RenderSvgToPngAsync(svg, png, 600, 600);
        if (!File.Exists(png)) throw new Exception("Render failed (no PNG produced).");

        var cfg = AppConfig.Load();
        cfg.CharacterImage = png;
        cfg.Save();
        progress.Report("Character ready ✓");
        return png;
    }

    private static async Task RenderSvgToPngAsync(string svg, string outPng, int w, int h)
    {
        string browser = Browsers.FirstOrDefault(File.Exists)
            ?? throw new Exception("No Chromium browser found to render the character.");

        string html = $"<!doctype html><html><body style='margin:0;background:transparent'>{svg}</body></html>";
        string htmlPath = Path.Combine(CharDir, "_char.html");
        await File.WriteAllTextAsync(htmlPath, html, new UTF8Encoding(false));
        try { File.Delete(outPng); } catch { }

        string fileUrl = "file:///" + htmlPath.Replace('\\', '/');
        var psi = new ProcessStartInfo(browser)
        {
            Arguments = $"--headless=new --disable-gpu --hide-scrollbars " +
                        $"--default-background-color=00000000 --force-device-scale-factor=2 " +
                        $"--screenshot=\"{outPng}\" --window-size={w},{h} \"{fileUrl}\"",
            UseShellExecute = false,
            CreateNoWindow  = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        using var p = Process.Start(psi)!;
        var se = p.StandardError.ReadToEndAsync();
        var so = p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        await se; await so;
    }
}
