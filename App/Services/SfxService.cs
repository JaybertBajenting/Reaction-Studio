using System.IO;
using System.Text.RegularExpressions;
using ReactionStudio.Models;

namespace ReactionStudio.Services;

// Locates the user's SFX library and picks sounds for specific moments.
// IMPORTANT: only GENERIC transition sounds (whoosh/swoosh/whip/throwing) are used by
// default — recognizable branded/meme/music SFX can trigger Content-ID just like the
// clips, which would undercut the transformativeness goal.
public static class SfxService
{
    // Resolve the SFX folder: config override, else auto-detect a sibling folder under
    // the app base whose name mentions "sfx" / "sound fx".
    public static string Folder()
    {
        var cfg = AppConfig.Load();
        if (!string.IsNullOrWhiteSpace(cfg.SfxFolder) && Directory.Exists(cfg.SfxFolder))
            return cfg.SfxFolder;
        try
        {
            foreach (var d in Directory.GetDirectories(AppPaths.Base))
                if (Regex.IsMatch(Path.GetFileName(d), @"sound\s*f|sfx", RegexOptions.IgnoreCase))
                    return d;
        }
        catch { }
        return "";
    }

    // Filename keywords that denote safe, generic sounds by category.
    private static readonly string[] WhipKeys   = { "whoosh", "swoosh", "swish", "whip", "throwing", "transition" };
    private static readonly string[] ImpactKeys = { "punch", "impact", "thud", "slam", "smash", "boom" };

    // Recognizable IP / brand / franchise / music terms — anything matching these is
    // EXCLUDED no matter what, because it can be Content-ID matched (defeats the point).
    private static readonly string[] Branded =
    {
        "sonic", "mario", "minecraft", "valorant", "naruto", "one-piece", "onepiece",
        "dragon", "dbz", "spongebob", "gta", "san-andreas", "among", "anime", "sukuna",
        "ryoiki", "jujutsu", "herta", "honkai", "genshin", "kobo", "hololive", "optimum",
        "optimus", "transformer", "taco", "spiderman", "illuminati", "mlg", "discord",
        "iphone", "samsung", "poco", "warkop", "upin", "ipin", "tf2", "yoshi", "bonk",
        "rizz", "sigma", "sheesh", "sax", "gandalf", "deja-vu", "song", "music", "theme",
        "ringtone", "remix", "rap", "ost", "meme", "tom", "acumalaka", "hitmarker"
    };

    // A random SAFE, generic sound matching any of `keys` (variety per render), or null.
    private static string? PickSafe(string[] keys)
    {
        string f = Folder();
        if (f == "") return null;
        try
        {
            var hits = Directory.GetFiles(f, "*.mp3", SearchOption.AllDirectories)
                .Where(p =>
                {
                    string n = Path.GetFileNameWithoutExtension(p).ToLowerInvariant();
                    return keys.Any(n.Contains) && !Branded.Any(n.Contains);
                }).ToList();
            if (hits.Count == 0) return null;
            return hits[Random.Shared.Next(hits.Count)];
        }
        catch { return null; }
    }

    public static string? WhipSfx()   => PickSafe(WhipKeys);    // generic whoosh on the whips
    public static string? ImpactSfx() => PickSafe(ImpactKeys);  // generic punch on the whip LANDS
}
