using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FanCurve.Core;
using ScottPlot.WinForms;

namespace FanCurvePanel;

sealed class CurveEditor : UserControl
{
    private readonly string _path;
    private readonly Func<string, (bool Found, double Temperature, double Percent)> _now;
    private readonly List<RulePane> _panes = [];
    private readonly Label _message = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Panel _body = new() { Dock = DockStyle.Fill };
    private AppConfig _config = new();
    private long _stamp;
    private bool _dirty;

    public CurveEditor(string path, Func<string, (bool Found, double Temperature, double Percent)> now)
    {
        _path = path;
        _now = now;
        Dock = DockStyle.Fill;
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(8, 4, 8, 4) };
        var apply = new Button { Text = "Aplicar", Dock = DockStyle.Right, Width = 108 };
        Theme.Button(apply);
        apply.Click += (_, _) => Apply();
        _message.Dock = DockStyle.Fill;
        _message.AutoEllipsis = true;
        _message.Height = 32;
        _message.Padding = new Padding(4, 0, 8, 0);
        _message.TextAlign = ContentAlignment.MiddleLeft;
        bar.Controls.Add(_message);
        bar.Controls.Add(apply);
        Controls.Add(_body);
        Controls.Add(bar);
        LoadFromDisk();
    }

    public void RefreshNow()
    {
        if (!_dirty && FileStamp() != _stamp)
        {
            LoadFromDisk();
            return;
        }

        foreach (var pane in _panes)
        {
            pane.Draw(_now(pane.RuleName));
        }
    }

    private void LoadFromDisk()
    {
        _config = AppConfig.Load(_path);
        _stamp = FileStamp();
        _dirty = false;
        _body.Controls.Clear();
        _panes.Clear();
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = Math.Max(1, _config.Rules.Count),
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var row = 0;
        foreach (var rule in _config.Rules)
        {
            stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var pane = new RulePane(rule, () => _dirty = true);
            pane.Dock = DockStyle.Fill;
            stack.Controls.Add(pane, 0, row);
            _panes.Add(pane);
            row++;
        }

        _body.Controls.Add(stack);
        _message.Text = "Arraste um ponto no gráfico. A temperatura continua em ordem crescente, e a curva só grava em Aplicar.";
        RefreshNow();
    }

    private void Apply()
    {
        foreach (var pane in _panes)
        {
            if (!pane.TryWriteCurve(out var error))
            {
                _message.Text = error;
                return;
            }
        }

        var errors = ConfigValidator.Validate(_config);
        if (errors.Count > 0)
        {
            _message.Text = string.Join(Environment.NewLine, errors);
            return;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(_config, AppConfig.JsonOptions);
        try
        {
            ConfigFiles.WriteAtomic(_path, bytes);
        }
        catch (UnauthorizedAccessException)
        {
            if (!TryElevated(bytes, out var elevated))
            {
                _message.Text = elevated;
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _message.Text = ex.Message;
            return;
        }

        _stamp = FileStamp();
        _dirty = false;
        _message.Text = "Curva gravada. Se o FanCurve estiver rodando, ela entra em até 5 s, sem reiniciar e sem tranco.";
    }

    private bool TryElevated(byte[] bytes, out string message)
    {
        var exe = WindowsStartup.ServiceExecutable();
        if (exe is null)
        {
            message = "A pasta da configuração pede administrador, e não achei o FanCurve.exe.";
            return false;
        }

        var temp = Path.Combine(Path.GetTempPath(), "fancurve-edit.json");
        ConfigFiles.WriteAtomic(temp, bytes);
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"apply-config \"{temp}\" --to \"{_path}\"",
                UseShellExecute = true,
                Verb = "runas",
            });
            if (process is null)
            {
                message = "O Windows não abriu a confirmação de administrador.";
                return false;
            }

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                message = "A gravação elevada não concluiu.";
                return false;
            }
        }
        catch (Win32Exception)
        {
            message = "A confirmação de administrador foi cancelada.";
            return false;
        }

        message = "";
        return true;
    }

    private long FileStamp()
    {
        var info = new FileInfo(_path);
        return info.Exists ? info.LastWriteTimeUtc.Ticks ^ info.Length : 0;
    }

    private sealed class RulePane : UserControl
    {
        private readonly FanRuleConfig _rule;
        private readonly DataGridView _grid = new()
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        };
        private readonly FormsPlot _plot = new() { Dock = DockStyle.Fill };
        private readonly Action _edited;
        private (bool Found, double Temperature, double Percent) _seen;
        private int _drag = -1;
        private bool _writing;
        private double _lockLeft;
        private double _lockRight;

        public RulePane(FanRuleConfig rule, Action edited)
        {
            _rule = rule;
            _edited = edited;
            ChartDraw.Prepare(_plot);
            Theme.Grid(_grid);
            _plot.MouseDown += OnPlotMouseDown;
            _plot.MouseMove += OnPlotMouseMove;
            _plot.MouseUp += (_, _) => EndDrag();
            _grid.RowTemplate.Height = 26;
            _grid.ColumnHeadersHeight = 28;
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            var gridHost = new TableLayoutPanel
            {
                Dock = DockStyle.Left,
                Width = 210,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(8, 4, 4, 4),
            };
            gridHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            gridHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            gridHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            gridHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            _grid.Columns.Add("temp", "°C");
            _grid.Columns.Add("percent", "%");
            foreach (var point in rule.Curve)
            {
                _grid.Rows.Add(Format(point.TemperatureC), Format(point.Percent));
            }

            _grid.CellValueChanged += (_, _) =>
            {
                if (_writing)
                {
                    return;
                }

                _edited();
                Draw(_seen);
            };
            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0) };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            var add = new Button { Text = "Adicionar", Dock = DockStyle.Fill, Margin = new Padding(0, 4, 4, 0) };
            var remove = new Button { Text = "Remover", Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 0) };
            Theme.Button(add);
            Theme.Button(remove);
            add.Click += (_, _) =>
            {
                var lastTemp = _grid.Rows.Count == 0 ? 40d : Read(_grid.Rows[^1].Cells[0].Value, 40);
                var lastPercent = _grid.Rows.Count == 0 ? 50d : Read(_grid.Rows[^1].Cells[1].Value, 50);
                _grid.Rows.Add(Format(lastTemp + 5), Format(lastPercent));
                _edited();
                Draw(_seen);
            };
            remove.Click += (_, _) =>
            {
                if (_grid.Rows.Count <= 2 || _grid.CurrentRow is null)
                {
                    return;
                }

                _grid.Rows.Remove(_grid.CurrentRow);
                _edited();
                Draw(_seen);
            };
            actions.Controls.Add(add, 0, 0);
            actions.Controls.Add(remove, 1, 0);
            var title = new Label
            {
                Text = rule.Name,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            };
            gridHost.Controls.Add(title, 0, 0);
            gridHost.Controls.Add(_grid, 0, 1);
            gridHost.Controls.Add(actions, 0, 2);
            Controls.Add(_plot);
            Controls.Add(gridHost);
        }

        public string RuleName => _rule.Name;

        public bool TryWriteCurve(out string error)
        {
            _grid.EndEdit();
            var points = new List<CurvePoint>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.IsNewRow)
                {
                    continue;
                }

                if (!TryRead(row.Cells[0].Value, out var temperature) || !TryRead(row.Cells[1].Value, out var percent))
                {
                    error = $"Ponto inválido na curva da regra {_rule.Name}.";
                    return false;
                }

                points.Add(new CurvePoint { TemperatureC = temperature, Percent = percent });
            }

            _rule.Curve = points;
            error = "";
            return true;
        }

        private void OnPlotMouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            var hit = HitTest(e.Location);
            if (hit < 0)
            {
                return;
            }

            var limits = _plot.Plot.Axes.GetLimits();
            _lockLeft = limits.Left;
            _lockRight = limits.Right;
            _drag = hit;
            _plot.Cursor = Cursors.SizeAll;
            var at = At(e.Location);
            MovePoint(hit, at.X, at.Y);
        }

        private void OnPlotMouseMove(object? sender, MouseEventArgs e)
        {
            if (_drag < 0)
            {
                _plot.Cursor = HitTest(e.Location) >= 0 ? Cursors.Hand : Cursors.Default;
                return;
            }

            var at = At(e.Location);
            MovePoint(_drag, at.X, at.Y);
        }

        private void EndDrag()
        {
            if (_drag < 0)
            {
                return;
            }

            _drag = -1;
            _plot.Cursor = Cursors.Default;
            Draw(_seen);
        }

        private void MovePoint(int index, double temperature, double percent)
        {
            if (index < 0 || index >= _grid.Rows.Count)
            {
                return;
            }

            percent = Math.Clamp(Math.Round(percent), 0, 100);
            var min = index == 0 ? 0 : Math.Floor(Read(_grid.Rows[index - 1].Cells[0].Value, 0)) + 1;
            var max = index == _grid.Rows.Count - 1 ? 120 : Math.Ceiling(Read(_grid.Rows[index + 1].Cells[0].Value, 120)) - 1;
            if (min > max)
            {
                return;
            }

            temperature = Math.Clamp(Math.Round(temperature), min, max);
            _writing = true;
            _grid.Rows[index].Cells[0].Value = Format(temperature);
            _grid.Rows[index].Cells[1].Value = Format(percent);
            _writing = false;
            _edited();
            Draw(_seen);
        }

        private int HitTest(Point location)
        {
            var best = -1;
            var bestDistance = 16d;
            for (var i = 0; i < _grid.Rows.Count; i++)
            {
                var row = _grid.Rows[i];
                if (row.IsNewRow
                    || !TryRead(row.Cells[0].Value, out var temperature)
                    || !TryRead(row.Cells[1].Value, out var percent))
                {
                    continue;
                }

                var pixel = ToClient(_plot.Plot.GetPixel(new ScottPlot.Coordinates(temperature, percent)));
                var dx = pixel.X - location.X;
                var dy = pixel.Y - location.Y;
                var distance = Math.Sqrt((dx * dx) + (dy * dy));
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            return best;
        }

        private ScottPlot.Coordinates At(Point location)
        {
            var (sx, sy) = RenderScale();
            return _plot.Plot.GetCoordinates(new ScottPlot.Pixel(location.X * sx, location.Y * sy));
        }

        private PointF ToClient(ScottPlot.Pixel pixel)
        {
            var (sx, sy) = RenderScale();
            return new PointF(pixel.X / sx, pixel.Y / sy);
        }

        private (float X, float Y) RenderScale()
        {
            var figure = _plot.Plot.RenderManager.LastRender.FigureRect;
            var width = Math.Max(1, _plot.ClientSize.Width);
            var height = Math.Max(1, _plot.ClientSize.Height);
            var sx = figure.Width / width;
            var sy = figure.Height / height;
            if (sx is < 0.5f or > 4f)
            {
                sx = 1;
            }

            if (sy is < 0.5f or > 4f)
            {
                sy = 1;
            }

            return (sx, sy);
        }

        public void Draw((bool Found, double Temperature, double Percent) now)
        {
            _seen = now;
            var xs = new List<double>();
            var ys = new List<double>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.IsNewRow)
                {
                    continue;
                }

                if (TryRead(row.Cells[0].Value, out var temperature) && TryRead(row.Cells[1].Value, out var percent))
                {
                    xs.Add(temperature);
                    ys.Add(percent);
                }
            }

            var plot = _plot.Plot;
            plot.Clear();
            if (xs.Count > 0)
            {
                var line = plot.Add.Scatter(xs.ToArray(), ys.ToArray());
                line.LegendText = _rule.Name;
                line.LineWidth = 2;
                line.MarkerSize = 11;
                line.Color = ChartDraw.PaletteColor(0);
            }

            if (now.Found)
            {
                var point = plot.Add.Scatter(new[] { now.Temperature }, new[] { now.Percent });
                point.LegendText = "agora";
                point.MarkerSize = 12;
                point.LineWidth = 0;
                point.Color = ScottPlot.Colors.Black;
            }

            plot.Axes.Bottom.Label.Text = "°C";
            plot.Axes.Left.Label.Text = "%";
            plot.Axes.SetLimitsY(-5, 105);
            if (_drag >= 0 && _lockRight > _lockLeft)
            {
                plot.Axes.SetLimitsX(_lockLeft, _lockRight);
            }
            plot.Layout.Fixed(new ScottPlot.PixelPadding(56, 16, 40, 10));
            plot.Legend.IsVisible = true;
            plot.Legend.Alignment = ScottPlot.Alignment.UpperLeft;
            _plot.Refresh();
        }

        private static string Format(double value) =>
            value.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR"));

        private static bool TryRead(object? value, out float number)
        {
            number = 0;
            var text = Convert.ToString(value, CultureInfo.CurrentCulture);
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            return float.TryParse(text, NumberStyles.Float, CultureInfo.GetCultureInfo("pt-BR"), out number)
                || float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        }

        private static double Read(object? value, double fallback) =>
            TryRead(value, out var number) ? number : fallback;
    }
}
