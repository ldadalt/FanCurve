namespace FanCurve;

public sealed class EventLogWriter : IDisposable
{
    private readonly string _path;
    private readonly object _gate = new();
    private bool _disposed;

    public EventLogWriter(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "events.log");
    }

    public string FilePath => _path;

    public void Write(string message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}";
        lock (_gate)
        {
            RotateIfNeeded();
            File.AppendAllText(_path, line);
        }
    }

    public void Dispose() => _disposed = true;

    private void RotateIfNeeded()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < 1024 * 1024)
        {
            return;
        }

        var previous = _path + ".old";
        File.Move(_path, previous, overwrite: true);
    }
}
