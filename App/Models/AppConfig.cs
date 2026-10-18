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

    // AI Character Commentary mode
    public string ElevenLabsApiKey  { get; set; } = "";
    public string ElevenLabsVoiceId { get; set; } = "";
    public string CharacterImage    { get; set; } = "";  // PNG used as the on-screen reactor

    // Transformativeness controls (commentary mode) — reduce Content-ID / reused-content risk.
    public double DuckVolume   { get; set; } = 0.10;  // original clip audio level under your voice (0 = silent)
    public bool   MuteOriginal { get; set; } = false; // fully mute the clip's audio (most transformative)
    public string MusicBedPath { get; set; } = "";    // optional royalty-free music file looped under the voice
    public double MusicVolume  { get; set; } = 0.12;  // music bed level (only used if MusicBedPath set)
    public bool   ReframeClip  { get; set; } = true;  // dynamic push-in/punch on the clip (your edit, not a replay)
    public int    Cutaways     { get; set; } = 2;     // mid-clip full-frame character reaction cutaways (0 = none)
    public string SfxFolder    { get; set; } = "";    // folder of your SFX; "" = auto-detect under the app base
    public bool   UseSfxWhoosh { get; set; } = true;  // use a real whoosh sound from SfxFolder on the whips (else synth)
    public bool   UseSfxImpact { get; set; } = true;  // layer a generic punch/impact on the whip LANDS (sound design)

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
