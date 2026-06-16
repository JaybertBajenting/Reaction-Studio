using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ReactionStudio;

/// <summary>Percentage (0-100) → arc Geometry for a circular progress ring.</summary>
public class PercentToArcConverter : IValueConverter
{
    public double Size   { get; set; } = 26;
    public double Radius { get; set; } = 10;

    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        double pct = value is double d ? d : 0;
        pct = Math.Clamp(pct, 0, 99.999);   // 100 would be degenerate (start == end)
        if (pct <= 0.01) return Geometry.Empty;

        double cx = Size / 2, cy = Size / 2, r = Radius;
        double angle = pct / 100.0 * 360.0;
        double rad   = angle * Math.PI / 180.0;

        var start = new Point(cx, cy - r);                                  // 12 o'clock
        var end   = new Point(cx + r * Math.Sin(rad), cy - r * Math.Cos(rad));
        bool large = angle > 180;

        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, large,
                                        SweepDirection.Clockwise, true));
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        geo.Freeze();
        return geo;
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>bool → Visibility. Set Invert=true to flip.</summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        bool b = value is bool x && x;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}
