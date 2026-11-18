using FanCurve.Core;
using ScottPlot;
using ScottPlot.Interactivity.UserActionResponses;

namespace FanCurvePanel;

static class ChartDraw
{
    private static readonly ScottPlot.Color[] Palette =
    [
        ScottPlot.Color.FromHex("#2563eb"),
        ScottPlot.Color.FromHex("#0f766e"),
        ScottPlot.Color.FromHex("#c2410c"),
        ScottPlot.Color.FromHex("#7c3aed"),
        ScottPlot.Color.FromHex("#0369a1"),
        ScottPlot.Color.FromHex("#3f6212"),
    ];

    public static ScottPlot.Color PaletteColor(int index) => Palette[index % Palette.Length];

    public static string Unit(ChannelKind kind) => kind switch
    {
        ChannelKind.TemperatureC => "°C",
        ChannelKind.Rpm => "RPM",
        ChannelKind.Percent or ChannelKind.LoadPercent => "%",
        ChannelKind.PowerW => "W",
        ChannelKind.MemoryGb => "GB",
        ChannelKind.ClockMhz => "MHz",
        _ => "valor",
    };

    public static void Draw(Plot plot, string legend, IReadOnlyList<long> times, IReadOnlyList<double> values, ScottPlot.Color color, long gapLimitMs)
    {
        var drawn = false;
        foreach (var (xs, ys) in Segments(times, values, gapLimitMs))
        {
            var line = plot.Add.SignalXY(xs, ys);
            line.Color = color;
            line.LineWidth = 1.8f;
            line.LegendText = drawn ? string.Empty : legend;
            drawn = true;
        }
    }

    public static void DrawBand(Plot plot, IReadOnlyList<long> times, IReadOnlyList<double> min, IReadOnlyList<double> max, ScottPlot.Color color, long gapLimitMs)
    {
        var count = Math.Min(times.Count, Math.Min(min.Count, max.Count));
        var xs = new List<double>();
        var lows = new List<double>();
        var highs = new List<double>();
        long previous = long.MinValue;
        for (var i = 0; i < count; i++)
        {
            var time = times[i];
            var gap = previous != long.MinValue && (time - previous > gapLimitMs || time < previous);
            if (gap || double.IsNaN(min[i]) || double.IsNaN(max[i]))
            {
                Flush();
                previous = time;
                if (double.IsNaN(min[i]) || double.IsNaN(max[i]))
                {
                    continue;
                }
            }

            xs.Add(ToPlot(time));
            lows.Add(min[i]);
            highs.Add(max[i]);
            previous = time;
        }

        Flush();

        void Flush()
        {
            if (xs.Count == 0)
            {
                return;
            }

            var fill = plot.Add.FillY(xs.ToArray(), lows.ToArray(), highs.ToArray());
            fill.FillColor = color.WithAlpha(48);
            fill.LineWidth = 0;
            fill.LegendText = string.Empty;
            xs.Clear();
            lows.Clear();
            highs.Clear();
        }
    }

    public static void Prepare(ScottPlot.WinForms.FormsPlot control)
    {
        var input = control.UserInputProcessor;
        input.DoubleLeftClickBenchmark(false);
        input.LeftClickDragPan(false);
        input.RightClickDragZoom(false);
        input.RemoveAll<MouseWheelZoom>();
        input.RemoveAll<MouseDragZoomRectangle>();
        input.RemoveAll<SingleClickAutoscale>();
        input.RemoveAll<SingleClickContextMenu>();
        input.RemoveAll<KeyboardPanAndZoom>();
        input.RemoveAll<KeyboardAutoscale>();
        Style(control.Plot);
    }

    public static void Style(Plot plot)
    {
        var paper = ScottPlot.Color.FromHex("#ffffff");
        var ink = ScottPlot.Color.FromHex("#667085");
        var line = ScottPlot.Color.FromHex("#e7edf2");
        plot.FigureBackground.Color = paper;
        plot.DataBackground.Color = paper;
        plot.Grid.MajorLineColor = line;
        plot.Grid.MajorLineWidth = 1;
        plot.Grid.MinorLineWidth = 0;
        plot.Axes.Color(ink);
        plot.Font.Set("Segoe UI");
        plot.Legend.OutlineStyle.Width = 0;
        plot.Legend.ShadowColor = ScottPlot.Colors.Transparent;
        plot.Legend.BackgroundColor = paper;
    }

    public static void FinishTimeAxis(Plot plot, string yLabel)
    {
        plot.Axes.DateTimeTicksBottom();
        plot.Axes.Left.Label.Text = yLabel;
        plot.Legend.IsVisible = plot.PlottableList.Count > 0;
        plot.Legend.Alignment = Alignment.UpperRight;
        Style(plot);
    }

    private static List<(double[] Xs, double[] Ys)> Segments(IReadOnlyList<long> times, IReadOnlyList<double> values, long gapLimitMs)
    {
        var result = new List<(double[], double[])>();
        var xs = new List<double>();
        var ys = new List<double>();
        long previous = long.MinValue;
        var count = Math.Min(times.Count, values.Count);
        for (var i = 0; i < count; i++)
        {
            var time = times[i];
            var value = values[i];
            var gap = previous != long.MinValue && (time - previous > gapLimitMs || time < previous);
            if (gap || double.IsNaN(value))
            {
                Flush();
                previous = time;
                if (double.IsNaN(value))
                {
                    continue;
                }
            }

            xs.Add(ToPlot(time));
            ys.Add(value);
            previous = time;
        }

        Flush();
        return result;

        void Flush()
        {
            if (xs.Count == 0)
            {
                return;
            }

            result.Add((xs.ToArray(), ys.ToArray()));
            xs.Clear();
            ys.Clear();
        }
    }

    public static double Time(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime.ToOADate();

    private static double ToPlot(long unixMs) => Time(unixMs);
}
