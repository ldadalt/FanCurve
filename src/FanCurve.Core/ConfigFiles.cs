namespace FanCurve.Core;

public static class ConfigFiles
{
    public static void WriteAtomic(string destination, ReadOnlySpan<byte> bytes)
    {
        var directory = Path.GetDirectoryName(destination) ?? throw new InvalidOperationException("Destino sem pasta.");
        Directory.CreateDirectory(directory);
        var temporary = destination + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, destination, overwrite: true);
    }
}
