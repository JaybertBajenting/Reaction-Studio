using OpenCvSharp;

namespace ReactionStudio.Services;

// Real directional/radial motion blur — the thing ffmpeg can't do. ffmpeg's
// gblur/avgblur/boxblur are all isotropic (they smear equally in every direction),
// so a fast zoom just turns to mush. A zoom ("radial") blur instead averages many
// copies of the frame scaled about a center point, which streaks outward from that
// center — the "whip" look. Crucially, strength is a free knob here; it isn't capped
// by how far the underlying zoom actually moved between rendered frames (which is why
// tmix-of-zoom-frames reads as soft ghosting rather than a punch).
public static class MotionBlurService
{
    // Radial zoom blur on a single frame. `samples` copies of the frame are scaled
    // about the center across a ±strength/2 spread and averaged (in float, so bright
    // areas don't clip/darken). strength 0.20 ≈ a ±10% scale spread. Centering the
    // spread on 1.0 keeps the average size unchanged (no per-frame size "pop").
    public static Mat ZoomBlur(Mat src, int samples, double strength)
    {
        int w = src.Width, h = src.Height;
        var center = new Point2f(w / 2f, h / 2f);
        var size = new Size(w, h);

        using var acc = new Mat(src.Size(), MatType.CV_32FC3, Scalar.All(0));
        using var warpedF = new Mat();
        for (int i = 0; i < samples; i++)
        {
            double frac = samples == 1 ? 0.0 : (double)i / (samples - 1) - 0.5;
            double s = 1.0 + strength * frac;                 // scale for this sample
            // angle 0 → pure scale about the center point.
            using var m = Cv2.GetRotationMatrix2D(center, 0, s);
            using var warped = new Mat();
            Cv2.WarpAffine(src, warped, m, size,
                           InterpolationFlags.Linear, BorderTypes.Replicate);
            warped.ConvertTo(warpedF, MatType.CV_32FC3);
            Cv2.Add(acc, warpedF, acc);
        }

        var outMat = new Mat();
        acc.ConvertTo(outMat, MatType.CV_8UC3, 1.0 / samples);  // average → back to 8-bit
        return outMat;
    }

    // Apply ZoomBlur frame-by-frame to a video. strengthAt(frameIndex) returns the
    // blur strength for that frame; 0 (or near-0) passes the frame through untouched,
    // so holds stay sharp and only the fast zooms get smeared. The intermediate codec
    // (mp4v) is throwaway — the caller re-encodes to H.264 afterward.
    public static void ZoomBlurVideo(string src, string dst, Func<int, double> strengthAt)
    {
        using var cap = new VideoCapture(src);
        if (!cap.IsOpened()) throw new Exception($"MotionBlur: cannot open {src}");
        int w = cap.FrameWidth, h = cap.FrameHeight;
        double fps = cap.Fps > 1 ? cap.Fps : 30;

        using var writer = new VideoWriter(dst, VideoWriter.FourCC('m', 'p', '4', 'v'),
                                           fps, new Size(w, h));
        if (!writer.IsOpened()) throw new Exception($"MotionBlur: cannot open writer {dst}");

        using var frame = new Mat();
        for (int i = 0; cap.Read(frame) && !frame.Empty(); i++)
        {
            double strength = strengthAt(i);
            int samples = Math.Min(28, (int)Math.Round(strength * 80) + 2);
            if (strength < 0.004 || samples < 3)
            {
                writer.Write(frame);
            }
            else
            {
                using var blurred = ZoomBlur(frame, samples, strength);
                writer.Write(blurred);
            }
        }
    }
}
