using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DiscImageStudio.Controls;

/// <summary>Shared disc drawing for the annulus preview and the design canvas. Both show the
/// same body/hub/content-ring diagram in millimetre space, so the geometry lives here instead
/// of being written twice with its own palette. Radii arrive in device pixels; callers own the
/// mm-to-pixel scale.</summary>
internal static class DiscRenderCore
{
    public static void DrawDiscBody(DrawingContext context, Point center, double discRadius, double hubRadius)
    {
        context.DrawEllipse(DiscTheme.DiscBody, null, center, discRadius, discRadius);
        context.DrawEllipse(DiscTheme.Hub, null, center, hubRadius, hubRadius);
    }

    /// <summary>Content ring as one EvenOdd fill: outer circle minus the inner circle.</summary>
    public static void DrawContentRing(DrawingContext context, Point center, double innerRadius, double outerRadius)
    {
        if (outerRadius <= innerRadius)
        {
            return;
        }

        PathGeometry ring = new() { FillRule = FillRule.EvenOdd };
        ring.Figures.Add(CircleFigure(center, outerRadius));
        ring.Figures.Add(CircleFigure(center, innerRadius));
        context.DrawGeometry(DiscTheme.Ring, null, ring);
    }

    public static void DrawGuideCircles(
        DrawingContext context,
        Point center,
        double discRadius,
        double outerRadius,
        double innerRadius)
    {
        Pen pen = new(DiscTheme.Guide, 1);
        context.DrawEllipse(null, pen, center, discRadius, discRadius);
        context.DrawEllipse(null, pen, center, outerRadius, outerRadius);
        if (innerRadius > 0)
        {
            context.DrawEllipse(null, pen, center, innerRadius, innerRadius);
        }
    }

    public static void DrawOriginCross(DrawingContext context, Point center, double arm)
    {
        Pen pen = new(DiscTheme.Guide, 1);
        context.DrawLine(pen, new Point(center.X - arm, center.Y), new Point(center.X + arm, center.Y));
        context.DrawLine(pen, new Point(center.X, center.Y - arm), new Point(center.X, center.Y + arm));
    }

    public static FormattedText CreateText(string text, double size, Brush brush, double pixelsPerDip) =>
        new(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            DiscTheme.Typeface,
            size,
            brush,
            pixelsPerDip);

    public static PathFigure CircleFigure(Point center, double radius)
    {
        PathFigure figure = new() { StartPoint = new Point(center.X - radius, center.Y), IsClosed = true };
        figure.Segments.Add(new ArcSegment(
            new Point(center.X + radius, center.Y),
            new Size(radius, radius),
            0,
            isLargeArc: true,
            SweepDirection.Clockwise,
            isStroked: false));
        figure.Segments.Add(new ArcSegment(
            new Point(center.X - radius, center.Y),
            new Size(radius, radius),
            0,
            isLargeArc: true,
            SweepDirection.Clockwise,
            isStroked: false));
        return figure;
    }

    public static PathFigure RectangleFigure(Rect rect)
    {
        PathFigure figure = new() { StartPoint = new Point(rect.Left, rect.Top), IsClosed = true };
        figure.Segments.Add(new LineSegment(new Point(rect.Right, rect.Top), isStroked: true));
        figure.Segments.Add(new LineSegment(new Point(rect.Right, rect.Bottom), isStroked: true));
        figure.Segments.Add(new LineSegment(new Point(rect.Left, rect.Bottom), isStroked: true));
        return figure;
    }
}
