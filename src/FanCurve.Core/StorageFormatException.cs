namespace FanCurve.Core;

public sealed class StorageFormatException : IOException
{
    public StorageFormatException(string message) : base(message)
    {
    }

    public StorageFormatException(string message, Exception inner) : base(message, inner)
    {
    }
}
