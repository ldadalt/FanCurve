using FanCurve.Core;

namespace FanCurvePanel;

sealed record PanelPaths(string DataDirectory, string LogDirectory, string? ConfigFile)
{
    public string HistoryDirectory => Path.Combine(DataDirectory, "hist");

    public string EventsFile => Path.Combine(LogDirectory, "events.log");

    public static PanelPaths Parse(string[] args)
    {
        string? data = null;
        string? logs = null;
        string? config = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--data" && i + 1 < args.Length)
            {
                data = args[++i];
            }
            else if (args[i] == "--logs" && i + 1 < args.Length)
            {
                logs = args[++i];
            }
            else if (args[i] == "--config" && i + 1 < args.Length)
            {
                config = args[++i];
            }
        }

        if (data is null && logs is null && config is null && !File.Exists(FanCurvePaths.ConfigFile))
        {
            var local = FindLocalConfig();
            if (local is not null)
            {
                var folder = Path.GetDirectoryName(local)!;
                return new PanelPaths(
                    Path.GetFullPath(Path.Combine(folder, "data")),
                    Path.GetFullPath(Path.Combine(folder, "logs")),
                    local);
            }
        }

        data ??= FanCurvePaths.DataDirectory;
        logs ??= FanCurvePaths.LogDirectory;
        return new PanelPaths(
            Path.GetFullPath(data),
            Path.GetFullPath(logs),
            config is null ? null : Path.GetFullPath(config));
    }

    private static string? FindLocalConfig()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "local", "fancurve.json");
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    public string? ResolveConfig()
    {
        if (ConfigFile is not null)
        {
            return ConfigFile;
        }

        if (File.Exists(FanCurvePaths.ConfigFile))
        {
            return FanCurvePaths.ConfigFile;
        }

        var sibling = Path.Combine(Directory.GetParent(DataDirectory)?.FullName ?? DataDirectory, "fancurve.json");
        return File.Exists(sibling) ? sibling : null;
    }
}
