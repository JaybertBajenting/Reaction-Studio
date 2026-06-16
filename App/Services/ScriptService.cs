using System.IO;

namespace ReactionStudio.Services;

/// <summary>
/// Generates and persists the spoken reaction script for a clip.
/// The script is cached in a sidecar file next to the clip
/// (e.g. "Video by x.mp4.script.txt") so it only needs to be made once.
/// </summary>
public static class ScriptService
{
    public static string ScriptPathFor(string clipPath) => clipPath + ".script.txt";

    public static bool HasScript(string clipPath) =>
        File.Exists(ScriptPathFor(clipPath)) &&
        new FileInfo(ScriptPathFor(clipPath)).Length > 0;

    public static string LoadScript(string clipPath) =>
        HasScript(clipPath) ? File.ReadAllText(ScriptPathFor(clipPath)) : "";

    /// <summary>Extract frames, ask Codex, and save the script. Returns it.</summary>
    public static async Task<string> GenerateAsync(string clipPath)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ReactionStudio");
        Directory.CreateDirectory(tempDir);
        string key = Path.GetFileNameWithoutExtension(clipPath);

        var frames = new List<string>();
        double[] ratios = { 0.15, 0.4, 0.65, 0.9 };
        for (int i = 0; i < ratios.Length; i++)
        {
            string f = Path.Combine(tempDir, $"sf_{key}_{i}.jpg");
            try
            {
                // 720px wide — sharper frames help the model read the scene accurately
                await FfmpegService.ExtractSmallFrameAsync(clipPath, f, ratios[i], 720);
                if (File.Exists(f)) frames.Add(f);
            }
            catch { }
        }

        string script = await CodexService.GenerateScriptAsync(frames);
        if (!string.IsNullOrWhiteSpace(script))
            await File.WriteAllTextAsync(ScriptPathFor(clipPath), script);

        foreach (var f in frames) { try { File.Delete(f); } catch { } }
        return script;
    }
}
