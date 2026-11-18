namespace FanCurve.Core;

public static class FanCurvePaths
{
    public static string ProgramDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FanCurve");

    public static string ProgramFilesRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FanCurve");

    public static string ConfigDirectory => Path.Combine(ProgramDataRoot, "config");

    public static string DataDirectory => Path.Combine(ProgramDataRoot, "data");

    public static string HistoryDirectory => Path.Combine(DataDirectory, "hist");

    public static string LogDirectory => Path.Combine(ProgramDataRoot, "logs");

    public static string ConfigFile => Path.Combine(ConfigDirectory, "fancurve.json");
}
