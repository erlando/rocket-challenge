namespace Rockets.Capture;

/// <summary>
/// Appends delivery attempts to an NDJSON file. Each line is flushed to the OS immediately,
/// so a hard-killed capture server loses at most the line being written.
/// </summary>
public sealed class CaptureLog : IDisposable
{
    private readonly Lock _gate = new();
    private readonly StreamWriter _writer;

    public CaptureLog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read));
    }

    public void Append(DateTimeOffset receivedAt, int attempt, int status, string body)
    {
        var line = CaptureRecord.Serialize(receivedAt, attempt, status, body);
        lock (_gate)
        {
            _writer.WriteLine(line);
            _writer.Flush();
        }
    }

    public void Dispose() => _writer.Dispose();
}
