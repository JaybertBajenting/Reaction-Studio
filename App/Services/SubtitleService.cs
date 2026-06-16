using System.IO;
using System.Text;

namespace ReactionStudio.Services;

/// <summary>
/// Turns word timestamps into a styled .ass subtitle file — short punchy chunks
/// (TikTok/Shorts style: bold white, thick black outline, lower-third center).
/// </summary>
public static class SubtitleService
{
    public static bool WriteAss(string assPath, List<CaptionWord> words)
    {
        if (words.Count == 0) return false;

        var sb = new StringBuilder();
        sb.Append(
            "[Script Info]\n" +
            "ScriptType: v4.00+\n" +
            "PlayResX: 1080\n" +
            "PlayResY: 1920\n" +
            "WrapStyle: 2\n" +
            "ScaledBorderAndShadow: yes\n\n" +
            "[V4+ Styles]\n" +
            "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, " +
            "BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, " +
            "BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n" +
            // bold white text, thick black outline + soft shadow, big single word, center-ish
            "Style: Cap,Arial,108,&H00FFFFFF,&H000000FF,&H00000000,&H80000000,-1,0,0,0,100,100,0,0,1,7,3,2,90,90,470,1\n\n" +
            "[Events]\n" +
            "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");

        foreach (var (text, start, end) in OneWordAtATime(words))
            sb.Append($"Dialogue: 0,{T(start)},{T(end)},Cap,,0,0,0,,{Escape(text)}\n");

        File.WriteAllText(assPath, sb.ToString(), new UTF8Encoding(false));
        return true;
    }

    // One word on screen at a time. Each word appears the instant it's spoken and
    // stays until the next word starts; during a long pause it disappears instead
    // of lingering.
    private const double MaxHold = 0.5;   // max seconds to linger across a silent gap

    private static IEnumerable<(string text, double start, double end)> OneWordAtATime(
        List<CaptionWord> words)
    {
        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            double start = w.Start;
            double end;
            if (i + 1 < words.Count)
            {
                double nextStart = words[i + 1].Start;
                // hold until the next word, unless there's a big gap (pause)
                end = (nextStart - w.End) > MaxHold ? w.End + 0.15 : nextStart;
            }
            else
            {
                end = w.End + 0.25;
            }
            if (end <= start) end = start + 0.1;   // guarantee visible
            yield return (w.Word, start, end);
        }
    }

    private static string Escape(string s) =>
        s.Replace("{", "(").Replace("}", ")").Replace("\r", " ").Replace("\n", " ");

    // seconds → H:MM:SS.cc (ASS time format)
    private static string T(double sec)
    {
        if (sec < 0) sec = 0;
        int cs = (int)Math.Round(sec * 100);
        int h = cs / 360000; cs %= 360000;
        int m = cs / 6000;   cs %= 6000;
        int s = cs / 100;    int c = cs % 100;
        return $"{h}:{m:D2}:{s:D2}.{c:D2}";
    }
}
