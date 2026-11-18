using System.Globalization;
using System.Text;
using FanCurve.Core;
using ScottPlot.WinForms;

namespace FanCurvePanel;

sealed class MainForm : Form
{
    private readonly PanelPaths _paths;
    private readonly LiveFeed _live = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly FlowLayoutPanel _cards = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(8, 8, 8, 0), BackColor = Color.FromArgb(244, 246, 248) };
    private readonly FormsPlot _tempPlot = new() { Dock = DockStyle.Fill };
    private readonly FormsPlot _percentPlot = new() { Dock = DockStyle.Fill };
    private readonly Panel _historyHost = new() { Dock = DockStyle.Fill };
    private readonly Label _historyNote = new() { Dock = DockStyle.Top, Height = 36, Padding = new Padding(12, 8, 12, 0) };
    private readonly DataGridView _stats = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = Color.White };
    private readonly Panel _fansHost = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private CurveEditor? _curveEditor;
    private readonly TextBox _events = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9f), WordWrap = false };
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusText = new()
    {
        Spring = true,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(8, 0, 8, 0),
        Margin = new Padding(4, 2, 0, 0),
    };
    private readonly ChannelRangeBar _historyBar;
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private string _liveChannelStamp = "";
    private string _cardStamp = "";
    private readonly List<(Label Name, Label Value, int Index)> _cardLabels = [];
    private readonly ConfigPage _config;
    private readonly CheckBox _startup;
    private readonly CheckBox _control = new() { Text = "Controlar as fans", AutoSize = true, Left = 12, Top = 8 };
    private bool _configLoaded;
    private readonly FanSession _session = new();
    private string? _controlError;
    private bool _startupBusy;
    private bool _controlBusy;

    public MainForm(PanelPaths paths)
    {
        _paths = paths;
        _config = new ConfigPage(_paths.ResolveConfig());
        _startup = _config.Startup;
        Text = "FanCurve";
        ChartDraw.Prepare(_tempPlot);
        ChartDraw.Prepare(_percentPlot);
        Width = 1180;
        Height = 760;
        MinimumSize = new Size(900, 600);
        Font = new Font("Segoe UI", 9f);
        BackColor = Color.FromArgb(244, 246, 248);
        _historyBar = new ChannelRangeBar("Atualizar", "Salvar CSV");
        _historyBar.ShowLimit();
        _historyBar.ActionRequested += (_, _) => ReloadHistory();
        _historyBar.SecondaryRequested += (_, _) => ExportCsv();
        BuildStatsGrid();
        _status.Padding = new Padding(6, 2, 6, 2);
        _status.SizingGrip = false;
        _status.BackColor = Theme.Paper;
        _statusText.ForeColor = Theme.Muted;
        _historyNote.ForeColor = Theme.Muted;
        BackColor = Theme.Page;
        Theme.Grid(_stats);
        Controls.Add(_tabs);
        Controls.Add(_status);
        _status.Items.Add(_statusText);
        _tabs.TabPages.Add(LivePage());
        _tabs.TabPages.Add(BrowsePage("Histórico", _historyBar, HistoryBody(), _historyNote));
        _tabs.TabPages.Add(FansPage());
        _tabs.TabPages.Add(new TabPage("Configuração") { Controls = { _config } });
        _tabs.TabPages.Add(EventsPage());
        _tabs.SelectedIndexChanged += (_, _) => OnTabChanged();
        Controls.Add(StartupBar());
        _timer.Tick += (_, _) => OnTick();
        Shown += (_, _) =>
        {
            RefreshStartupBox();
            StartControlIfWanted();
            OnTick();
            _timer.Start();
        };
        FormClosing += (_, _) => StopOwnedControl();
        FormClosed += (_, _) =>
        {
            _timer.Dispose();
            _live.Dispose();
            _session.Dispose();
        };
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        SyncTimer();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        SyncTimer();
    }

    private void SyncTimer()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        var visible = Visible && WindowState != FormWindowState.Minimized;
        if (!visible)
        {
            _timer.Stop();
            return;
        }

        if (!_timer.Enabled)
        {
            _timer.Start();
            OnTick();
        }
    }

    private void OnTabChanged()
    {
        var title = _tabs.SelectedTab?.Text;
        if (title == "Histórico")
        {
            ReloadHistory();
        }
        else if (title == "Fans")
        {
            RefreshFans();
        }
        else if (title == "Configuração" && !_configLoaded)
        {
            _configLoaded = true;
            _config.Reload();
        }
        else if (title == "Eventos")
        {
            RefreshEvents();
        }
    }

    private Control StartupBar()
    {
        var bar = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = Color.White };
        _startup.CheckedChanged += (_, _) =>
        {
            if (_startupBusy)
            {
                return;
            }

            ApplyStartup(_startup.Checked);
        };
        _control.CheckedChanged += (_, _) =>
        {
            if (_controlBusy)
            {
                return;
            }

            if (_control.Checked)
            {
                StartControl();
                if (_control.Checked)
                {
                    FanSession.SaveAutoStart(true);
                }
            }
            else
            {
                StopControl();
                FanSession.SaveAutoStart(false);
            }
        };
        bar.Controls.Add(_control);
        return bar;
    }

    private void StartControlIfWanted()
    {
        var already = FanSession.IsRunning();
        var want = already || FanSession.LoadAutoStart();
        _controlBusy = true;
        _control.Checked = want;
        _controlBusy = false;
        if (want)
        {
            StartControl();
        }
    }

    private void StartControl()
    {
        var config = _paths.ResolveConfig();
        var exe = WindowsStartup.ServiceExecutable();
        if (config is null || !File.Exists(config))
        {
            SetControlChecked(false);
            _statusText.Text = "Não há fancurve.json para ligar o controle.";
            return;
        }

        if (exe is null)
        {
            SetControlChecked(false);
            _statusText.Text = "Não achei o FanCurve.exe.";
            return;
        }

        if (!_session.TryStart(exe, config, _paths.DataDirectory, _paths.LogDirectory, out var message))
        {
            SetControlChecked(false);
            _statusText.Text = message;
            return;
        }

        _statusText.Text = message;
    }

    private void StopControl()
    {
        try
        {
            _session.RequestStop(_paths.DataDirectory);
            _statusText.Text = "Controle desligado. A fan volta ao driver em até 2 s.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetControlChecked(true);
            _statusText.Text = "Não consegui desligar o controle. " + ex.Message;
        }
    }

    private void StopOwnedControl()
    {
        if (!_session.Owned)
        {
            return;
        }

        try
        {
            _session.RequestStop(_paths.DataDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _statusText.Text = "Não consegui devolver a fan. " + ex.Message;
        }
    }

    private void SetControlChecked(bool value)
    {
        _controlBusy = true;
        _control.Checked = value;
        _controlBusy = false;
    }

    private void RefreshStartupBox()
    {
        _startupBusy = true;
        _startup.Checked = WindowsStartup.IsEnabled();
        _startupBusy = false;
    }

    private void ApplyStartup(bool enabled)
    {
        _startup.Enabled = false;
        try
        {
            if (!WindowsStartup.TrySet(enabled, _paths.ResolveConfig(), out var message))
            {
                RefreshStartupBox();
            }

            _statusText.Text = message;
        }
        finally
        {
            _startup.Enabled = true;
        }
    }

    private TabPage LivePage()
    {
        var page = new TabPage("Agora");
        var split = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 2 };
        split.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        split.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        split.Controls.Add(_cards, 0, 0);
        split.SetColumnSpan(_cards, 2);
        split.Controls.Add(_tempPlot, 0, 1);
        split.Controls.Add(_percentPlot, 1, 1);
        page.Controls.Add(split);
        return page;
    }

    private static TabPage BrowsePage(string title, ChannelRangeBar bar, Control body, Label note)
    {
        var page = new TabPage(title);
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.Controls.Add(bar, 0, 0);
        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(body);
        right.Controls.Add(note);
        grid.Controls.Add(right, 1, 0);
        page.Controls.Add(grid);
        return page;
    }

    private Control HistoryBody()
    {
        var body = new Panel { Dock = DockStyle.Fill };
        var stats = new Panel { Dock = DockStyle.Bottom, Height = 128, Padding = new Padding(8, 4, 8, 8) };
        stats.Controls.Add(_stats);
        body.Controls.Add(_historyHost);
        body.Controls.Add(stats);
        return body;
    }

    private TabPage FansPage()
    {
        var page = new TabPage("Fans");
        page.Controls.Add(_fansHost);
        return page;
    }

    private TabPage EventsPage()
    {
        var page = new TabPage("Eventos");
        page.Controls.Add(_events);
        return page;
    }

    private void BuildStatsGrid()
    {
        _stats.ColumnHeadersHeight = 32;
        _stats.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _stats.RowTemplate.Height = 26;
        foreach (var name in new[] { "Canal", "Amostras", "Média", "Máximo", "p95", "Acima do limite", "Em 100%" })
        {
            var column = _stats.Columns[_stats.Columns.Add(name, name)];
            column.MinimumWidth = name == "Canal" ? 110 : 64;
        }
    }

    private void OnTick()
    {
        if (WindowState == FormWindowState.Minimized || !Visible)
        {
            _timer.Stop();
            return;
        }

        var failure = _session.FailureText();
        if (failure is not null)
        {
            _controlError = failure;
            if (_control.Checked)
            {
                SetControlChecked(false);
            }
        }

        _live.Refresh(_paths.DataDirectory);
        if (_tabs.SelectedTab?.Text == "Agora")
        {
            RefreshCards();
            RefreshLivePlots();
        }
        else
        {
            RefreshStatus();
        }

        RememberChannels();
        if (_tabs.SelectedTab?.Text == "Eventos")
        {
            RefreshEvents();
        }
    }

    private void RefreshCards()
    {
        if (_live.Error is not null && !_live.HasReader)
        {
            _statusText.Text = _live.Error;
            return;
        }

        var last = _live.Times.Count - 1;
        var shown = new List<int>();
        for (var i = 0; i < _live.Channels.Count; i++)
        {
            if (_live.Channels[i].Kind is not ChannelKind.Internal && !IsOwnProcess(_live.Channels[i]))
            {
                shown.Add(i);
            }
        }

        var stamp = string.Join("|", shown.Select(i => SeriesLoader.Key(_live.Channels[i])));
        if (stamp != _cardStamp)
        {
            _cardStamp = stamp;
            _cards.SuspendLayout();
            _cards.Controls.Clear();
            _cardLabels.Clear();
            foreach (var index in shown)
            {
                var (panel, name, value) = Card();
                _cardLabels.Add((name, value, index));
                _cards.Controls.Add(panel);
            }

            _cards.ResumeLayout();
        }

        for (var n = 0; n < _cardLabels.Count; n++)
        {
            var index = shown.Count == _cardLabels.Count ? shown[n] : _cardLabels[n].Index;
            var channel = _live.Channels[index];
            var value = last >= 0 && index < _live.Channels.Count ? _live.Column(index)[last] : double.NaN;
            var failing = IsFailing(channel);
            var name = failing ? channel.Name + " · fail-safe" : channel.Name;
            var number = FormatCard(channel, value);
            if (_cardLabels[n].Name.Text != name)
            {
                _cardLabels[n].Name.Text = name;
            }

            if (_cardLabels[n].Value.Text != number)
            {
                _cardLabels[n].Value.Text = number;
            }

            _cardLabels[n].Value.ForeColor = failing ? Color.Firebrick : SystemColors.ControlText;
        }

        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (_live.Error is not null && !_live.HasReader)
        {
            SetStatus(_live.Error);
            return;
        }

        var ageMs = _live.HasReader
            ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _live.Status.LastTickUnixMs
            : long.MaxValue;
        var self = SelfText();
        if (!_live.HasReader)
        {
            SetStatus(_controlError ?? _live.Error ?? "Sem anel.");
            return;
        }

        _controlError = null;
        var running = ageMs >= -2000 && ageMs < 5000;
        var state = running
            ? "Rodando"
            : "Parado há " + SampleStats.Duration(Math.Max(0, ageMs) / 1000d);
        var sensors = _live.Status.FailsafeMask == 0 ? "sensores ok" : "sensor ausente";
        SetStatus($"{state} · {sensors} · {_live.Status.WriteCount} amostras{self}");
    }

    private void SetStatus(string text)
    {
        if (_statusText.Text != text)
        {
            _statusText.Text = text;
        }
    }

    private string SelfText()
    {
        double cpu = double.NaN;
        double memory = double.NaN;
        var last = _live.Times.Count - 1;
        if (last < 0)
        {
            return "";
        }

        for (var i = 0; i < _live.Channels.Count; i++)
        {
            if (_live.Channels[i].SourceId == "fancurve:cpu")
            {
                cpu = _live.Column(i)[last];
            }
            else if (_live.Channels[i].SourceId == "fancurve:mem")
            {
                memory = _live.Column(i)[last];
            }
        }

        var culture = CultureInfo.GetCultureInfo("pt-BR");
        var cpuText = double.IsNaN(cpu) ? "—" : cpu.ToString("0.00", culture) + "%";
        var memText = double.IsNaN(memory) ? "—" : (memory * 1024).ToString("0", culture) + " MB";
        return $" · processo {cpuText} de um núcleo, {memText}";
    }

    private bool IsFailing(ChannelDesc channel)
    {
        if (!channel.SourceId.StartsWith("rule:", StringComparison.Ordinal) || !channel.SourceId.EndsWith(":out", StringComparison.Ordinal))
        {
            return false;
        }

        var index = 0;
        foreach (var item in _live.Channels)
        {
            if (!item.SourceId.EndsWith(":out", StringComparison.Ordinal))
            {
                continue;
            }

            if (SeriesLoader.Same(item, channel))
            {
                return index < 31 && (_live.Status.FailsafeMask & (1u << index)) != 0;
            }

            index++;
        }

        return false;
    }

    private static string FormatCard(ChannelDesc channel, double value)
    {
        if (channel.Kind == ChannelKind.MemoryGb && !double.IsNaN(value) && Math.Abs(value) < 1)
        {
            return (value * 1024).ToString("0", CultureInfo.GetCultureInfo("pt-BR")) + " MB";
        }

        return SampleStats.Number(value, channel.Kind) + " " + ChartDraw.Unit(channel.Kind);
    }

    private static (Panel Panel, Label Name, Label Value) Card()
    {
        var panel = new Panel { Width = 168, Height = 64, Margin = new Padding(0, 0, 8, 8), BackColor = Color.White };
        var name = new Label { Dock = DockStyle.Top, Height = 18, Padding = new Padding(8, 4, 8, 0), ForeColor = Color.FromArgb(90, 98, 110) };
        var number = new Label
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 14f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 8, 4),
            BackColor = Color.White,
        };
        panel.Controls.Add(number);
        panel.Controls.Add(name);
        return (panel, name, number);
    }

    private long _drawnTick = long.MinValue;
    private int _drawnCount;

    private void RefreshLivePlots()
    {
        var count = _live.Times.Count;
        var tick = count == 0 ? long.MinValue : _live.Times[count - 1];
        if (tick == _drawnTick && count == _drawnCount)
        {
            return;
        }

        _drawnTick = tick;
        _drawnCount = count;
        DrawLive(_tempPlot, static channel => channel.Kind == ChannelKind.TemperatureC, "°C", 8);
        DrawLive(_percentPlot, static channel => channel.Kind == ChannelKind.Percent, "%", 20);
    }

    private void DrawLive(FormsPlot control, Func<ChannelDesc, bool> include, string unit, double minSpan)
    {
        var plot = control.Plot;
        plot.Clear();
        var color = 0;
        for (var i = 0; i < _live.Channels.Count; i++)
        {
            var channel = _live.Channels[i];
            if (!include(channel))
            {
                continue;
            }

            ChartDraw.Draw(plot, channel.Name, _live.Times, _live.Column(i), ChartDraw.PaletteColor(color), FanCurveLimits.GapLimitMs);
            color++;
        }

        ChartDraw.FinishTimeAxis(plot, unit + " · 10 min");
        if (color > 0)
        {
            plot.Axes.AutoScale();
            var limits = plot.Axes.GetLimits();
            if (limits.Top - limits.Bottom < minSpan)
            {
                var mid = (limits.Top + limits.Bottom) / 2;
                plot.Axes.SetLimitsY(mid - (minSpan / 2), mid + (minSpan / 2));
            }

            if (plot.Axes.Bottom.TickGenerator is ScottPlot.TickGenerators.DateTimeAutomatic ticks)
            {
                ticks.LabelFormatter = static date => date.ToString("HH:mm");
            }
        }

        plot.Layout.Fixed(new ScottPlot.PixelPadding(56, 16, 40, 8));

        control.Refresh();
    }

    private void RememberChannels()
    {
        if (!_live.HasReader)
        {
            return;
        }

        var liveStamp = string.Join("|", _live.Channels.Select(SeriesLoader.Key));
        if (liveStamp == _liveChannelStamp)
        {
            return;
        }

        try
        {
            var channels = new List<ChannelDesc>(_live.Channels);
            foreach (var table in HistoryReader.ListChannelTables(_paths.HistoryDirectory))
            {
                foreach (var channel in table.Channels)
                {
                    if (!channels.Exists(existing => SeriesLoader.Same(existing, channel)))
                    {
                        channels.Add(channel);
                    }
                }
            }

            channels.RemoveAll(IsOwnProcess);
            _liveChannelStamp = liveStamp;
            _historyBar.SetChannels(channels);
            _drawnTick = long.MinValue;
            if (_tabs.SelectedTab?.Text == "Histórico")
            {
                OnTabChanged();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StorageFormatException)
        {
            _historyNote.Text = ex.Message;
        }
    }

    private void ReloadHistory()
    {
        try
        {
            var (from, to) = _historyBar.Range();
            var series = SeriesLoader.Load(_paths.DataDirectory, _paths.HistoryDirectory, from, to, _historyBar.Selected());
            var minute = series.Count > 0 && series[0].MinuteResolution;
            var threshold = (double)_historyBar.Limit.Value;
            var limit = threshold.ToString("0.#", CultureInfo.GetCultureInfo("pt-BR"));
            var window = _historyBar.WindowText();
            _historyNote.Text = minute
                ? $"Média por minuto. Buraco acima de 2 min corta a linha. {window}."
                : $"Uma amostra por segundo. Buraco acima de 2 s corta a linha. {window}.";
            FillPlots(_historyHost, series, minute ? 90_000 : FanCurveLimits.GapLimitMs, from, to);
            FillStats(series, threshold);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StorageFormatException or ArgumentException)
        {
            _historyNote.Text = ex.Message;
        }
    }

    private void FillStats(IReadOnlyList<LoadedSeries> series, double threshold)
    {
        _stats.Rows.Clear();
        foreach (var item in series)
        {
            var summary = SampleStats.Of(item, threshold);
            var full = item.Channel.Kind is ChannelKind.Percent or ChannelKind.LoadPercent
                ? SampleStats.Duration(summary.SecondsAtFull)
                : "—";
            _stats.Rows.Add(
                item.Channel.Name,
                summary.Count,
                SampleStats.Number(summary.Mean, item.Channel.Kind),
                SampleStats.Number(summary.Max, item.Channel.Kind),
                SampleStats.Number(summary.P95, item.Channel.Kind),
                SampleStats.Duration(summary.SecondsAbove),
                full);
        }
    }

    private static bool IsOwnProcess(ChannelDesc channel) =>
        channel.SourceId is "fancurve:cpu" or "fancurve:mem";

    private void ExportCsv()
    {
        try
        {
            var selected = _historyBar.Selected();
            if (selected.Count == 0)
            {
                _historyNote.Text = "Marque ao menos um canal.";
                return;
            }

            using var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "fancurve.csv" };
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            var (from, to) = _historyBar.Range();
            var series = SeriesLoader.Load(_paths.DataDirectory, _paths.HistoryDirectory, from, to, selected);
            WriteCsv(dialog.FileName, series);
            _historyNote.Text = "Arquivo gravado: " + dialog.FileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StorageFormatException or ArgumentException)
        {
            _historyNote.Text = ex.Message;
        }
    }

    private static void WriteCsv(string path, IReadOnlyList<LoadedSeries> series)
    {
        var culture = CultureInfo.GetCultureInfo("pt-BR");
        var times = new SortedSet<long>();
        var lookup = new Dictionary<long, double>[series.Count];
        for (var i = 0; i < series.Count; i++)
        {
            lookup[i] = [];
            for (var n = 0; n < series[i].Times.Length; n++)
            {
                times.Add(series[i].Times[n]);
                lookup[i][series[i].Times[n]] = series[i].Values[n];
            }
        }

        var builder = new StringBuilder();
        builder.Append("hora");
        foreach (var item in series)
        {
            builder.Append(';').Append(item.Channel.Name);
        }

        builder.AppendLine();
        foreach (var time in times)
        {
            builder.Append(DateTimeOffset.FromUnixTimeMilliseconds(time).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", culture));
            for (var i = 0; i < series.Count; i++)
            {
                builder.Append(';');
                if (lookup[i].TryGetValue(time, out var value) && !double.IsNaN(value))
                {
                    builder.Append(value.ToString("0.###", culture));
                }
            }

            builder.AppendLine();
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static void FillPlots(Panel host, IReadOnlyList<LoadedSeries> series, long gapLimitMs, long fromUnixMs, long toUnixMs)
    {
        host.Controls.Clear();
        if (series.Count == 0 || series.All(static item => item.Times.Length == 0))
        {
            host.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Sem amostras neste período.", TextAlign = ContentAlignment.MiddleCenter });
            return;
        }

        var groups = series.GroupBy(static item => ChartDraw.Unit(item.Channel.Kind)).ToList();
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = groups.Count,
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var plots = new List<FormsPlot>();
        var row = 0;
        foreach (var group in groups)
        {
            stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / groups.Count));
            var control = new FormsPlot { Dock = DockStyle.Fill, Margin = new Padding(4) };
            ChartDraw.Prepare(control);
            var plot = control.Plot;
            plot.Clear();
            var color = 0;
            foreach (var item in group)
            {
                var paint = ChartDraw.PaletteColor(color++);
                if (item.Min is not null && item.Max is not null)
                {
                    ChartDraw.DrawBand(plot, item.Times, item.Min, item.Max, paint, gapLimitMs);
                }

                ChartDraw.Draw(plot, item.Channel.Name, item.Times, item.Values, paint, gapLimitMs);
            }

            ChartDraw.FinishTimeAxis(plot, group.Key);
            if (toUnixMs > fromUnixMs)
            {
                plot.Axes.SetLimitsX(ChartDraw.Time(fromUnixMs), ChartDraw.Time(toUnixMs));
            }

            var padding = new ScottPlot.PixelPadding(58, 20, 44, 10);
            plot.Layout.Fixed(padding);
            stack.Controls.Add(control, 0, row);
            plots.Add(control);
            row++;
        }

        if (plots.Count > 1)
        {
            for (var i = 1; i < plots.Count; i++)
            {
                plots[i].Plot.Axes.Link(plots[0], x: true, y: false);
                plots[0].Plot.Axes.Link(plots[i], x: true, y: false);
            }
        }

        host.Controls.Add(stack);
        foreach (var control in plots)
        {
            control.Refresh();
        }
    }

    private void RefreshFans()
    {
        var configPath = _paths.ResolveConfig();
        if (configPath is null || !File.Exists(configPath))
        {
            _fansHost.Controls.Clear();
            _curveEditor = null;
            _fansHost.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Não achei o fancurve.json.", TextAlign = ContentAlignment.MiddleCenter });
            return;
        }

        if (_curveEditor is null || _curveEditor.IsDisposed)
        {
            _fansHost.Controls.Clear();
            _curveEditor = new CurveEditor(configPath, NowOf);
            _fansHost.Controls.Add(_curveEditor);
        }

        _curveEditor.RefreshNow();
    }

    private (bool Found, double Temperature, double Percent) NowOf(string ruleName)
    {
        var found = TryCurrentPoint(ruleName, out var temperature, out var percent);
        return (found, temperature, percent);
    }

    private bool TryCurrentPoint(string ruleName, out double temperature, out double percent)
    {
        temperature = double.NaN;
        percent = double.NaN;
        var last = _live.Times.Count - 1;
        if (last < 0)
        {
            return false;
        }

        for (var i = 0; i < _live.Channels.Count; i++)
        {
            var id = _live.Channels[i].SourceId;
            if (id == "rule:" + ruleName + ":in")
            {
                temperature = _live.Column(i)[last];
            }
            else if (id == "rule:" + ruleName + ":out")
            {
                percent = _live.Column(i)[last];
            }
        }

        return !double.IsNaN(temperature) && !double.IsNaN(percent);
    }

    private void RefreshEvents()
    {
        if (!File.Exists(_paths.EventsFile))
        {
            _events.Text = "Sem eventos.";
            return;
        }

        var lines = new List<string>();
        using (var stream = new FileStream(_paths.EventsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }
        }

        if (lines.Count > 300)
        {
            lines.RemoveRange(0, lines.Count - 300);
        }

        var text = string.Join(Environment.NewLine, lines);
        if (_events.Text != text)
        {
            _events.Text = text;
            _events.SelectionStart = _events.TextLength;
            _events.ScrollToCaret();
        }
    }
}
