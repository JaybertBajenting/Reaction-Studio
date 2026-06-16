using NAudio.Wave;

namespace ReactionStudio.Services;

public record MicDevice(int Index, string Name);

public static class AudioService
{
    /// <summary>
    /// Lists capture devices. Index -1 is the Windows default ("WAVE_MAPPER"),
    /// then each WaveIn device by its real index.
    /// </summary>
    public static List<MicDevice> ListMics()
    {
        var mics = new List<MicDevice> { new(-1, "Default (Windows)") };
        try
        {
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                try
                {
                    var caps = WaveInEvent.GetCapabilities(i);
                    mics.Add(new MicDevice(i, caps.ProductName));
                }
                catch { }
            }
        }
        catch { }
        return mics;
    }
}
