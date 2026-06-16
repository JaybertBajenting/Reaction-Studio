using System.IO;
using System.Text.Json;

namespace ReactionStudio.Models;

public class AppConfig
{
    public int    CamIndex    { get; set; } = 0;
    public int    MicIndex    { get; set; } = -1;   // -1 = Windows default capture device
    public double MicVolume   { get; set; } = 2.0;  // your voice gain in compose (2.0 = 200%)
    public bool   IncludeCaption { get; set; } = true;  // burn the hook caption onto the video
    public bool   BlurProfanity  { get; set; } = true;  // auto-blur swear words baked into the clip
    public bool   AutoCaptions   { get; set; } = true;  // burn word-captions of your voice (whisper)

    // Scheduled publishing: queue uploads to go public at 2 peak times/day (US Eastern).
    public bool   SchedulePublic   { get; set; } = true;
    public int    PeakHourDay      { get; set; } = 12;   // noon ET
    public int    PeakHourNight    { get; set; } = 21;   // 9 PM ET
    public string ScheduleTimeZone { get; set; } = "Eastern Standard Time";
    public string LastScheduledUtc { get; set; } = "";   // rolling cursor so uploads queue 2/day

    public string AnthropicKey{ get; set; } = "";

    private static readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ReactionStudio", "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_path)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
