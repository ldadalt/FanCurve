using System.ComponentModel;
using System.Diagnostics;
using FanCurve.Core;

namespace FanCurvePanel;

static class WindowsStartup
{
    public static bool IsEnabled()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks", "/Query /TN \\FanCurve")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return false;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    public static bool TrySet(bool enabled, string? config, out string message)
    {
        var exe = FindExe();
        if (exe is null)
        {
            message = "Não achei o FanCurve.exe para gravar a opção.";
            return false;
        }

        var arguments = enabled
            ? "startup on" + (string.IsNullOrWhiteSpace(config) ? "" : $" --config \"{config}\"")
            : "startup off";
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
            });
            if (process is null)
            {
                message = "O Windows não abriu a confirmação de administrador.";
                return false;
            }

            process.WaitForExit();
        }
        catch (Win32Exception)
        {
            message = "A confirmação de administrador foi cancelada.";
            return false;
        }

        var now = IsEnabled();
        if (now != enabled)
        {
            message = "A opção não mudou.";
            return false;
        }

        message = enabled
            ? "O FanCurve vai iniciar na próxima vez que o Windows ligar."
            : "A inicialização automática foi desligada. Um FanCurve que já esteja em execução continua até o desligamento.";
        return true;
    }

    public static string? ServiceExecutable() => FindExe();

    private static string? FindExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var published = Path.Combine(dir.FullName, "publish", "service", "FanCurve.exe");
            if (File.Exists(published))
            {
                return published;
            }
        }

        var installed = Path.Combine(FanCurvePaths.ProgramFilesRoot, "FanCurve.exe");
        if (File.Exists(installed))
        {
            return installed;
        }

        var beside = Path.Combine(AppContext.BaseDirectory, "FanCurve.exe");
        return File.Exists(beside) ? beside : null;
    }
}
