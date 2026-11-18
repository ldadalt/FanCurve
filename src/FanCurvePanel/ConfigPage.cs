using System.Text.Json;
using FanCurve.Core;

namespace FanCurvePanel;

sealed class ConfigPage : UserControl
{
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        BackgroundColor = SystemColors.Window,
        BorderStyle = BorderStyle.None,
    };

    private readonly Label _summary = new() { Dock = DockStyle.Top, Height = 52, Padding = new Padding(8, 6, 8, 0) };
    private readonly Button _refresh = new() { Text = "Atualizar", AutoSize = true, Left = 8, Top = 6, FlatStyle = FlatStyle.Flat };
    private readonly CheckBox _everything = new()
    {
        Text = "Mostrar sensores sem leitura",
        AutoSize = true,
        Left = 120,
        Top = 8,
    };
    private readonly string? _configPath;
    private int _generation;
    private IReadOnlyList<DetectedSensor> _rows = [];

    public CheckBox Startup { get; } = new()
    {
        Text = "Ligar o controle quando o Windows iniciar",
        AutoSize = true,
        Left = 12,
        Top = 28,
    };

    public ConfigPage(string? configPath)
    {
        _configPath = configPath;
        Dock = DockStyle.Fill;
        BackColor = Theme.Page;
        Theme.Button(_refresh);
        Theme.Grid(_grid);
        _summary.ForeColor = Theme.Muted;
        BuildGrid();
        Controls.Add(BuildLayout());
        _refresh.Click += (_, _) => Reload();
        _everything.CheckedChanged += (_, _) => Show(_rows);
    }

    public void Reload()
    {
        var generation = ++_generation;
        _refresh.Enabled = false;
        _summary.Text = "Lendo o hardware deste PC. Isso leva alguns segundos.";
        _grid.Rows.Clear();
        var exe = WindowsStartup.ServiceExecutable();
        if (exe is null)
        {
            _refresh.Enabled = true;
            _summary.Text = "Não achei o FanCurve.exe para ler os sensores.";
            return;
        }

        Task.Run(() => SensorInventory.Read(exe)).ContinueWith(task =>
        {
            if (IsDisposed || !IsHandleCreated || generation != _generation)
            {
                return;
            }

            BeginInvoke(() =>
            {
                if (generation != _generation)
                {
                    return;
                }

                _refresh.Enabled = true;
                if (task.IsFaulted)
                {
                    _summary.Text = task.Exception?.GetBaseException().Message ?? "Não consegui ler o hardware.";
                    return;
                }

                _rows = task.Result;
                Show(_rows);
            });
        });
    }

    private void Show(IReadOnlyList<DetectedSensor> rows)
    {
        var useful = SensorInventory.Useful(rows, MetricIds());
        var visible = _everything.Checked ? rows : useful;
        _grid.Rows.Clear();
        foreach (var row in visible)
        {
            _grid.Rows.Add(
                SensorInventory.FriendlyType(row.Type),
                SensorInventory.DisplayName(row, visible),
                SensorInventory.FormatReading(row),
                row.Id);
        }

        var summary = SensorInventory.PlainSummary(useful);
        _summary.Text = _everything.Checked
            ? summary + $" Lista completa: {rows.Count}."
            : summary;
    }

    private void BuildGrid()
    {
        _grid.Columns.Add("type", "Tipo");
        _grid.Columns.Add("name", "Nome");
        _grid.Columns.Add("value", "Agora");
        _grid.Columns.Add("id", "Identificador");
        _grid.Columns["value"]!.FillWeight = 40;
        _grid.Columns["type"]!.FillWeight = 70;
        _grid.Columns["name"]!.FillWeight = 90;
        _grid.Columns["id"]!.FillWeight = 160;
    }

    private Control BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(StartupGroup(), 0, 0);
        var bar = new Panel { Dock = DockStyle.Fill };
        bar.Controls.Add(_everything);
        bar.Controls.Add(_refresh);
        layout.Controls.Add(bar, 0, 1);

        var list = new Panel { Dock = DockStyle.Fill };
        list.Controls.Add(_grid);
        list.Controls.Add(_summary);
        layout.Controls.Add(list, 0, 2);
        return layout;
    }

    private IReadOnlyList<string> MetricIds()
    {
        if (string.IsNullOrWhiteSpace(_configPath) || !File.Exists(_configPath))
        {
            return [];
        }

        try
        {
            return AppConfig.Load(_configPath).Metrics;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private Control StartupGroup()
    {
        var file = string.IsNullOrWhiteSpace(_configPath) ? "" : _configPath;
        var row = new Panel { Dock = DockStyle.Fill };
        Startup.Dock = DockStyle.Left;
        Startup.Top = 6;
        var path = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(16, 0, 8, 0),
            ForeColor = Color.FromArgb(90, 98, 110),
            Text = file,
        };
        row.Controls.Add(path);
        row.Controls.Add(Startup);
        return row;
    }
}
