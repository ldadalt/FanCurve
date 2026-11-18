using System.Diagnostics;

namespace FanCurvePanel;

readonly record struct DetectedSensor(string Type, string Name, string Value, string Id);

static class SensorInventory
{
    public static IReadOnlyList<DetectedSensor> Read(string exe)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "list",
            WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("O Windows não abriu o FanCurve.exe.");

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? "Não consegui ler o hardware." : error.Trim();
            throw new InvalidOperationException(detail);
        }

        var rows = new List<DetectedSensor>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split("  ", 4, StringSplitOptions.None);
            if (parts.Length < 4 || !parts[0].StartsWith('/'))
            {
                continue;
            }

            rows.Add(new DetectedSensor(parts[1], parts[3], parts[2], parts[0]));
        }

        rows.Sort(static (a, b) =>
        {
            var order = Rank(a.Type).CompareTo(Rank(b.Type));
            return order != 0 ? order : string.CompareOrdinal(a.Name, b.Name);
        });
        return rows;
    }

    public static string FriendlyType(string type) => type switch
    {
        "Control" => "Controle de fan",
        "Fan" => "Rotação",
        "Temperature" => "Temperatura",
        "Load" => "Carga",
        "Power" => "Potência",
        "Clock" => "Clock",
        "Voltage" => "Tensão",
        "Data" => "Dado",
        _ => type,
    };

    public static bool HasReading(DetectedSensor row) =>
        row.Value.Length > 0 && row.Value is not "—" and not "-";

    public static List<DetectedSensor> Useful(IReadOnlyList<DetectedSensor> rows, IReadOnlyCollection<string>? recordedIds = null)
    {
        var recorded = recordedIds is null
            ? null
            : new HashSet<string>(recordedIds, StringComparer.Ordinal);
        var useful = new List<DetectedSensor>();
        foreach (var row in rows)
        {
            var inHistory = recorded is not null && recorded.Contains(row.Id);
            if (row.Type is "Control" or "Fan"
                || (row.Type == "Temperature" && HasReading(row))
                || (inHistory && row.Type is "Load" or "Power" && HasReading(row)))
            {
                useful.Add(row);
            }
        }

        return useful;
    }

    public static string PlainSummary(IReadOnlyList<DetectedSensor> useful)
    {
        var controls = new List<DetectedSensor>();
        var temperatures = new List<DetectedSensor>();
        foreach (var row in useful)
        {
            if (row.Type == "Control")
            {
                controls.Add(row);
            }
            else if (row.Type == "Temperature")
            {
                temperatures.Add(row);
            }
        }

        if (controls.Count == 0)
        {
            return "Nenhuma fan aceita controle neste PC.";
        }

        var fan = controls.Count == 1
            ? $"{controls[0].Name} ({controls[0].Value}%)"
            : $"{controls.Count} fans";
        var temperature = temperatures.Count switch
        {
            0 => "Nenhuma temperatura com leitura.",
            _ => "Temperaturas: " + string.Join(", ", temperatures.Select(static row => row.Name + " " + row.Value + " °C")) + ".",
        };
        var loads = useful.Where(static row => row.Type == "Load").Select(static row => row.Name).ToList();
        var powers = useful.Where(static row => row.Type == "Power").Select(static row => row.Name).ToList();
        var also = new List<string>();
        if (loads.Count > 0)
        {
            also.Add("carga " + string.Join(", ", loads));
        }

        if (powers.Count > 0)
        {
            also.Add("potência " + string.Join(", ", powers));
        }

        var extra = also.Count == 0 ? "" : " No histórico: " + string.Join("; ", also) + ".";
        return $"Dá para controlar {fan}. {temperature}{extra}";
    }

    public static string DisplayName(DetectedSensor row, IReadOnlyList<DetectedSensor> visible)
    {
        foreach (var other in visible)
        {
            if (other.Type != row.Type && string.Equals(other.Name, row.Name, StringComparison.Ordinal))
            {
                return row.Name + " · " + KindWord(row.Type);
            }
        }

        return row.Name;
    }

    public static string FormatReading(DetectedSensor row)
    {
        if (!HasReading(row))
        {
            return row.Value;
        }

        return row.Type switch
        {
            "Control" or "Load" => row.Value + " %",
            "Temperature" => row.Value + " °C",
            "Power" => row.Value + " W",
            "Clock" => row.Value + " MHz",
            _ => row.Value,
        };
    }

    private static string KindWord(string type) => type switch
    {
        "Temperature" => "temperatura",
        "Load" => "carga",
        "Power" => "potência",
        "Control" => "controle",
        "Fan" => "rotação",
        "Clock" => "clock",
        _ => FriendlyType(type).ToLowerInvariant(),
    };

    private static int Rank(string type) => type switch
    {
        "Control" => 0,
        "Fan" => 1,
        "Temperature" => 2,
        "Load" => 3,
        "Power" => 4,
        _ => 5,
    };
}
