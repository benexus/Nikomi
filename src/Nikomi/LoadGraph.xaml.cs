using Nikomi.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Nikomi;

/// <summary>
/// A CPU load graph: one filled line per unbroken run of measurements, horizontal grid lines at every
/// quarter and vertical ones where the caller puts them. Redrawn whenever the data or the size changes.
/// </summary>
public sealed partial class LoadGraph : UserControl
{
    private const int Rows = 4;

    private IReadOnlyList<IReadOnlyList<GraphPoint>> _lines = Array.Empty<IReadOnlyList<GraphPoint>>();
    private IReadOnlyList<GraphTick> _ticks = Array.Empty<GraphTick>();
    private double? _marker;

    /// <summary>The pointer moved over the graph (how far across, 0 to 1) or left it (null).</summary>
    public event Action<double?>? Hovered;

    public LoadGraph()
    {
        InitializeComponent();
        Surface.SizeChanged += (_, _) => Redraw();
        Surface.PointerMoved += (_, e) =>
        {
            if (Surface.ActualWidth > 0) Hovered?.Invoke(Math.Clamp(e.GetCurrentPoint(Surface).Position.X / Surface.ActualWidth, 0, 1));
        };
        Surface.PointerExited += (_, _) => Hovered?.Invoke(null);
        Surface.PointerCanceled += (_, _) => Hovered?.Invoke(null);
    }

    public void SetData(IReadOnlyList<IReadOnlyList<GraphPoint>> lines, IReadOnlyList<GraphTick> ticks)
    {
        _lines = lines;
        _ticks = ticks;
        Redraw();
    }

    /// <summary>Draws a vertical marker at this far across (0 to 1), or removes it.</summary>
    public void SetMarker(double? x)
    {
        _marker = x;
        DrawMarker();
    }

    private void Redraw()
    {
        double w = Surface.ActualWidth, h = Surface.ActualHeight;
        if (w <= 0 || h <= 0) return;
        Surface.Clip = new RectangleGeometry { Rect = new Rect(0, 0, w, h) };

        // Grid lines on whole pixels plus a half, so a 1-pixel line stays sharp.
        static double Snap(double v) => Math.Floor(v) + 0.5;
        var grid = new GeometryGroup();
        for (int i = 1; i < Rows; i++)
        {
            double y = Snap(h * i / Rows);
            grid.Children.Add(new LineGeometry { StartPoint = new Point(0, y), EndPoint = new Point(w, y) });
        }
        foreach (var t in _ticks)
        {
            double x = Snap(t.X * w);
            if (x > 1 && x < w - 1) grid.Children.Add(new LineGeometry { StartPoint = new Point(x, 0), EndPoint = new Point(x, h) });
        }
        GridPath.Data = grid;

        // The line keeps a pixel clear of the top and bottom, so 0% and 100% are not cut in half.
        Point At(GraphPoint p) => new(p.X * w, 1 + (1 - p.Y) * (h - 2));
        var fill = new PathGeometry();
        var line = new PathGeometry();
        foreach (var points in _lines)
        {
            if (points.Count < 2) continue;
            var stroke = new PolyLineSegment();
            var area = new PolyLineSegment();
            Point first = At(points[0]), last = first;
            area.Points.Add(first);
            for (int i = 1; i < points.Count; i++)
            {
                last = At(points[i]);
                stroke.Points.Add(last);
                area.Points.Add(last);
            }
            area.Points.Add(new Point(last.X, h));
            line.Figures.Add(new PathFigure { StartPoint = first, IsClosed = false, IsFilled = false, Segments = { stroke } });
            fill.Figures.Add(new PathFigure { StartPoint = new Point(first.X, h), IsClosed = true, IsFilled = true, Segments = { area } });
        }
        FillPath.Data = fill;
        LinePath.Data = line;

        Labels.Children.Clear();
        bool labelled = false;
        foreach (var t in _ticks)
        {
            if (t.Label is not { Length: > 0 } text) continue;
            labelled = true;
            var tb = new TextBlock { Text = text, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tb, Math.Clamp(t.X * w - tb.DesiredSize.Width / 2, 0, Math.Max(0, w - tb.DesiredSize.Width)));
            Canvas.SetTop(tb, 2);
            Labels.Children.Add(tb);
        }
        Labels.Visibility = labelled ? Visibility.Visible : Visibility.Collapsed;
        DrawMarker();
    }

    private void DrawMarker()
    {
        double w = Surface.ActualWidth, h = Surface.ActualHeight;
        if (_marker is not { } m || w <= 0)
        {
            Marker.Visibility = Visibility.Collapsed;
            return;
        }
        double x = Math.Floor(m * w) + 0.5;
        Marker.X1 = Marker.X2 = x;
        Marker.Y1 = 0;
        Marker.Y2 = h;
        Marker.Visibility = Visibility.Visible;
    }
}
