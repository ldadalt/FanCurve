namespace FanCurve.Core;

/// <summary>
/// Impede dois escritores no mesmo diretório. O lock de arquivo vale entre
/// a sessão 0 (SYSTEM) e o console, sem o privilégio de um mutex Global.
/// </summary>
public sealed class SingleWriterLock : IDisposable
{
    private readonly FileStream _stream;
    private bool _disposed;

    private SingleWriterLock(FileStream stream)
    {
        _stream = stream;
    }

    public static SingleWriterLock Acquire(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, RingFiles.LockName);
        var stream = new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
        try
        {
            stream.Lock(0, 1);
        }
        catch (IOException)
        {
            stream.Dispose();
            throw new IOException("Outro FanCurve já está gravando nesta pasta.");
        }

        return new SingleWriterLock(stream);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _stream.Unlock(0, 1);
        }
        catch (IOException)
        {
        }

        _stream.Dispose();
    }
}
