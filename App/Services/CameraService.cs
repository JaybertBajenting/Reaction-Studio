using System.Diagnostics;
using OpenCvSharp;

namespace ReactionStudio.Services;

public record CameraDevice(int Index, string Name);

public static class CameraService
{
    public static List<CameraDevice> ListCameras()
    {
        // Get names from WMI
        var wmiNames = GetWmiNames();
        var found    = new List<CameraDevice>();

        for (int i = 0; i < 8; i++)
        {
            using var cap = new VideoCapture(i);
            if (!cap.IsOpened()) continue;
            using var mat = new Mat();
            if (!cap.Read(mat) || mat.Empty()) continue;
            string name = i < wmiNames.Count ? wmiNames[i] : $"Camera {i}";
            found.Add(new CameraDevice(i, name));
        }

        return found;
    }

    private static List<string> GetWmiNames()
    {
        try
        {
            var psi = new ProcessStartInfo("powershell")
            {
                Arguments =
                    "-Command \"Get-WmiObject Win32_PnPEntity | " +
                    "Where-Object {$_.PNPClass -eq 'Camera' -or $_.PNPClass -eq 'Image'} | " +
                    "Select-Object -ExpandProperty Name\"",
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                         .Select(s => s.Trim())
                         .Where(s => s.Length > 0)
                         .ToList();
        }
        catch
        {
            return [];
        }
    }
}
