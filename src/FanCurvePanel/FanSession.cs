using System.Diagnostics;
using System.Text;

namespace FanCurvePanel;

sealed class FanSession : IDisposable
{
    private readonly object _errorGate = new();
    private readonly StringBuilder _error = new();
    private Process? _process;
    private bool _disposed;

    public bool Owned { get; private set; }

    public static bool IsRunning() => Process.GetProcessesByName("FanCurve").Length > 0;

    public static bool LoadAutoStart()
    {
        try
        {
            var path = PreferencePath();
            if (!File.Exists(path))
            {
                return true;
            }

            return File.ReadAllText(path).Trim() != "0";
        }
        catch (IOException)
        {
            return true;
        }
    }

    public static void SaveAutoStart(bool enabled)
    {
        try
        {
            var path = PreferencePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, enabled ? "1" : "0");
        }
        catch (IOException)
        {
        }
    }

    public bool TryStart(string exe, string config, string data, string logs, out string message)
    {
        if (IsRunning())
        {
            Owned = false;
            message = "O controle já estava ligado. Fechar a janela deixa esse processo rodando.";
            return true;
        }

        _error.Clear();
        try
        {
            var stop = Path.Combine(data, "stop.request");
            if (File.Exists(stop))
            {
                File.Delete(stop);
            }
        }
        catch (IOException)
        {
        }

        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = $"run \"{config}\" --data \"{data}\" --logs \"{logs}\"",
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
                EnableRaisingEvents = true,
            };
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (string.IsNullOrWhiteSpace(eventArgs.Data))
                {
                    return;
                }

                lock (_errorGate)
                {
                    _error.AppendLine(eventArgs.Data);
                }
            };
            process.OutputDataReceived += (_, _) => { };
            if (!process.Start())
            {
                message = "O Windows não abriu o FanCurve.exe.";
                return false;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            Owned = true;
            message = "Controle ligado. Fechar esta janela devolve a fan ao driver.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            message = ex.Message;
            return false;
        }
    }

    public string? FailureText()
    {
        if (_process is null || !_process.HasExited || _process.ExitCode == 0)
        {
            return null;
        }

        string text;
        lock (_errorGate)
        {
            text = _error.ToString().Trim();
        }
        return string.IsNullOrWhiteSpace(text) ? "O controle encerrou." : text;
    }

    public void RequestStop(string dataDirectory)
    {
        var running = _process is { HasExited: false } || IsRunning();
        if (running)
        {
            var path = Path.Combine(dataDirectory, "stop.request");
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllBytes(path, "stop"u8);
            if (_process is not null)
            {
                _process.WaitForExit(4000);
            }
            else
            {
                var deadline = Environment.TickCount64 + 4000;
                while (IsRunning() && Environment.TickCount64 < deadline)
                {
                    Thread.Sleep(200);
                }
            }
        }

        Owned = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _process?.Dispose();
        _process = null;
    }

    private static string PreferencePath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FanCurve", "control-on-open.txt");
}
