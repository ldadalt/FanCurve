using System.Globalization;
using FanCurve.Core;

namespace FanCurvePanel;

sealed class ChannelRangeBar : UserControl
{
    private static readonly (string Label, long? SpanMs)[] Periods =
    [
        ("1 h", 60L * 60 * 1000),
        ("6 h", 6L * 60 * 60 * 1000),
        ("24 h", SeriesLoader.DayMs),
        ("7 d", 7 * SeriesLoader.DayMs),
        ("30 d", 30 * SeriesLoader.DayMs),
        ("Personalizado", null),
    ];

    private readonly ComboBox _period = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Button _back = new() { Text = "←", Width = 36, Dock = DockStyle.Fill };
    private readonly Button _forward = new() { Text = "→", Width = 36, Dock = DockStyle.Fill };
    private int _pagesBack;
    private readonly DateTimePicker _from = new() { Dock = DockStyle.Top, Format = DateTimePickerFormat.Custom, CustomFormat = "dd/MM/yyyy HH:mm" };
    private readonly DateTimePicker _to = new() { Dock = DockStyle.Top, Format = DateTimePickerFormat.Custom, CustomFormat = "dd/MM/yyyy HH:mm" };
    private readonly CheckedListBox _channels = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    private readonly HashSet<string> _checked = new(StringComparer.Ordinal);
    private string _shown = "";

    public ChannelRangeBar(string action, string? secondary = null)
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(8);
        Button? extra = null;
        if (secondary is not null)
        {
            extra = new Button { Text = secondary, Dock = DockStyle.Bottom, Height = 32 };
            extra.Click += (_, _) => SecondaryRequested?.Invoke(this, EventArgs.Empty);
        }

        var button = new Button { Text = action, Dock = DockStyle.Bottom, Height = 32 };
        button.Click += (_, _) => ActionRequested?.Invoke(this, EventArgs.Empty);
        var limitLabel = new Label { Text = "Limite", Dock = DockStyle.Bottom, Height = 18, Visible = false };
        Limit = new NumericUpDown
        {
            Dock = DockStyle.Bottom,
            DecimalPlaces = 1,
            Minimum = 0,
            Maximum = 1000,
            Value = 80,
            Visible = false,
        };
        _from.Value = DateTime.Now.AddHours(-1);
        _to.Value = DateTime.Now;
        foreach (var (label, _) in Periods)
        {
            _period.Items.Add(label);
        }

        _period.SelectedIndex = 0;
        _period.SelectedIndexChanged += (_, _) =>
        {
            _pagesBack = 0;
            UpdateCustomVisibility();
            ActionRequested?.Invoke(this, EventArgs.Empty);
        };
        _back.Click += (_, _) => Shift(1);
        _forward.Click += (_, _) => Shift(-1);
        Theme.Button(_back);
        Theme.Button(_forward);
        Theme.Button(button);
        if (extra is not null)
        {
            Theme.Button(extra);
        }

        var nav = new TableLayoutPanel { Dock = DockStyle.Top, Height = 32, ColumnCount = 3, Margin = new Padding(0) };
        nav.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        nav.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
        nav.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
        nav.Controls.Add(_period, 0, 0);
        nav.Controls.Add(_back, 1, 0);
        nav.Controls.Add(_forward, 2, 0);
        var caption = new Label { Text = "Canais", Dock = DockStyle.Top, Height = 22 };
        Controls.Add(_channels);
        Controls.Add(caption);
        Controls.Add(limitLabel);
        Controls.Add(Limit);
        Controls.Add(button);
        if (extra is not null)
        {
            Controls.Add(extra);
        }

        Controls.Add(_to);
        Controls.Add(_from);
        Controls.Add(nav);
        LimitLabel = limitLabel;
        UpdateCustomVisibility();
    }

    public NumericUpDown Limit { get; }

    public Label LimitLabel { get; }

    public event EventHandler? ActionRequested;

    public event EventHandler? SecondaryRequested;

    public void ShowLimit()
    {
        Limit.Visible = true;
        LimitLabel.Visible = true;
        LimitLabel.Text = "Acima de";
    }

    public void SetChannels(IReadOnlyList<ChannelDesc> channels)
    {
        var incoming = string.Join("|", channels.Select(SeriesLoader.Key));
        if (incoming == _shown)
        {
            return;
        }

        _shown = incoming;
        var previous = SelectedKeys();
        if (previous.Count > 0)
        {
            _checked.Clear();
            foreach (var key in previous)
            {
                _checked.Add(key);
            }
        }

        _channels.Items.Clear();
        var anyChecked = false;
        foreach (var channel in channels)
        {
            var item = new ChannelItem(channel);
            var index = _channels.Items.Add(item);
            var check = _checked.Contains(SeriesLoader.Key(channel));
            if (_checked.Count == 0)
            {
                check = channel.Kind is ChannelKind.TemperatureC or ChannelKind.Percent or ChannelKind.Rpm;
            }

            _channels.SetItemChecked(index, check);
            anyChecked |= check;
        }

        if (!anyChecked && _channels.Items.Count > 0)
        {
            _channels.SetItemChecked(0, true);
        }
    }

    public List<ChannelDesc> Selected()
    {
        var selected = new List<ChannelDesc>();
        foreach (ChannelItem item in _channels.CheckedItems)
        {
            selected.Add(item.Desc);
        }

        return selected;
    }

    public (long FromUnixMs, long ToUnixMs) Range()
    {
        var span = Periods[_period.SelectedIndex].SpanMs;
        if (span is null)
        {
            var from = new DateTimeOffset(_from.Value).ToUnixTimeMilliseconds();
            var to = new DateTimeOffset(_to.Value).ToUnixTimeMilliseconds();
            return (from, to);
        }

        var end = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (_pagesBack * span.Value);
        return (end - span.Value, end);
    }

    public string WindowText()
    {
        var (from, to) = Range();
        var start = DateTimeOffset.FromUnixTimeMilliseconds(from).LocalDateTime;
        var finish = DateTimeOffset.FromUnixTimeMilliseconds(to).LocalDateTime;
        if (start.Date == finish.Date)
        {
            return start.ToString("HH:mm") + " – " + finish.ToString("HH:mm");
        }

        return start.ToString("dd/MM HH:mm") + " – " + finish.ToString("dd/MM HH:mm");
    }

    public bool MinuteResolution => Range().ToUnixMs - Range().FromUnixMs > SeriesLoader.DayMs;

    private void Shift(int pages)
    {
        if (Periods[_period.SelectedIndex].SpanMs is null)
        {
            return;
        }

        _pagesBack = Math.Max(0, _pagesBack + pages);
        UpdateCustomVisibility();
        ActionRequested?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateCustomVisibility()
    {
        var custom = Periods[_period.SelectedIndex].SpanMs is null;
        _from.Visible = custom;
        _to.Visible = custom;
        _back.Enabled = !custom;
        _forward.Enabled = !custom && _pagesBack > 0;
    }

    private HashSet<string> SelectedKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (ChannelItem item in _channels.CheckedItems)
        {
            keys.Add(SeriesLoader.Key(item.Desc));
        }

        return keys;
    }

    private sealed record ChannelItem(ChannelDesc Desc)
    {
        public override string ToString() =>
            string.IsNullOrWhiteSpace(Desc.Name) ? Desc.SourceId : Desc.Name + " (" + ChartDraw.Unit(Desc.Kind) + ")";
    }
}

static class SampleStats
{
    public readonly record struct Summary(int Count, double Mean, double Max, double P95, double SecondsAbove, double SecondsAtFull);

    public static Summary Of(LoadedSeries series, double threshold)
    {
        var values = new List<double>();
        double above = 0;
        double atFull = 0;
        for (var i = 0; i < series.Values.Length; i++)
        {
            var value = series.Values[i];
            if (double.IsNaN(value))
            {
                continue;
            }

            values.Add(value);
            var step = series.MinuteResolution ? 60d : 1d;
            if (value > threshold)
            {
                above += step;
            }

            if (series.SamplesAt100 is not null)
            {
                atFull += series.SamplesAt100[i];
            }
            else if (value >= 99.5)
            {
                atFull += step;
            }
        }

        if (values.Count == 0)
        {
            return new Summary(0, double.NaN, double.NaN, double.NaN, 0, 0);
        }

        values.Sort();
        var mean = 0d;
        foreach (var value in values)
        {
            mean += value;
        }

        mean /= values.Count;
        return new Summary(values.Count, mean, values[^1], Percentile(values, 0.95), above, atFull);
    }

    public static string Duration(double seconds)
    {
        if (double.IsNaN(seconds))
        {
            return "—";
        }

        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (time.TotalHours >= 1)
        {
            return $"{(int)time.TotalHours} h {time.Minutes} min";
        }

        if (time.TotalMinutes >= 1)
        {
            return $"{(int)time.TotalMinutes} min {time.Seconds} s";
        }

        return $"{time.Seconds} s";
    }

    public static string Number(double value, ChannelKind kind)
    {
        if (double.IsNaN(value))
        {
            return "—";
        }

        var culture = CultureInfo.GetCultureInfo("pt-BR");
        return kind switch
        {
            ChannelKind.Rpm => value.ToString("0", culture),
            ChannelKind.MemoryGb => value.ToString("0.00", culture),
            ChannelKind.Percent or ChannelKind.LoadPercent => value.ToString("0.0", culture),
            _ => value.ToString("0.0", culture),
        };
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 1)
        {
            return sorted[0];
        }

        var rank = (sorted.Count - 1) * p;
        var low = (int)Math.Floor(rank);
        var high = (int)Math.Ceiling(rank);
        if (low == high)
        {
            return sorted[low];
        }

        var fraction = rank - low;
        return (sorted[low] * (1 - fraction)) + (sorted[high] * fraction);
    }
}
