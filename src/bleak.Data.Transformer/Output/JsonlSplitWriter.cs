namespace bleak.Data.Transformer.Output;

/// <summary>
/// Writes JSONL records to one or more output files, rolling to a new file every
/// <c>recordsPerFile</c> records. Files are written as <c>{baseName}_{timestamp}_{ordinal}.jsonl</c>;
/// if only a single file ends up being produced, the ordinal suffix is dropped on close so the
/// output is simply <c>{baseName}_{timestamp}.jsonl</c> (per spec: ordinal only when split).
///
/// Records are written as raw UTF-8 bytes (typically straight from a <c>Utf8JsonWriter</c> buffer)
/// followed by a line feed, so it never materializes the whole document in memory.
/// </summary>
public sealed class JsonlSplitWriter : IDisposable
{
    private readonly string _folder;
    private readonly string _baseName;
    private readonly string _timestamp;
    private readonly long _recordsPerFile;

    private FileStream? _current;
    private string? _currentPath;
    private long _recordsInCurrentFile;
    private readonly List<string> _partPaths = new();

    public JsonlSplitWriter(string folder, string baseName, string timestamp, long recordsPerFile)
    {
        if (recordsPerFile <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(recordsPerFile), "recordsPerFile must be greater than zero.");
        }
        _folder = folder;
        _baseName = baseName;
        _timestamp = timestamp;
        _recordsPerFile = recordsPerFile;
        Directory.CreateDirectory(folder);
    }

    /// <summary>Total records written across all files.</summary>
    public long RecordsWritten { get; private set; }

    /// <summary>Number of output files produced so far.</summary>
    public int FilesWritten => _partPaths.Count;

    private static readonly byte[] Newline = { (byte)'\n' };

    /// <summary>Writes one record (UTF-8 JSON, no trailing newline) and the record separator.</summary>
    public void WriteLine(ReadOnlySpan<byte> jsonUtf8)
    {
        if (_current is null || _recordsInCurrentFile >= _recordsPerFile)
        {
            RollToNextFile();
        }

        _current!.Write(jsonUtf8);
        _current.Write(Newline);
        _recordsInCurrentFile++;
        RecordsWritten++;
    }

    private void RollToNextFile()
    {
        _current?.Dispose();
        var ordinal = _partPaths.Count + 1;
        _currentPath = Path.Combine(_folder, $"{_baseName}_{_timestamp}_{ordinal}.jsonl");
        _current = new FileStream(_currentPath, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 1 << 20);
        _partPaths.Add(_currentPath);
        _recordsInCurrentFile = 0;
    }

    /// <summary>The final file paths, valid after <see cref="Dispose"/> (reflects the ordinal-drop rename).</summary>
    public IReadOnlyList<string> FinalPaths { get; private set; } = Array.Empty<string>();

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _current?.Dispose();
        _current = null;

        if (_partPaths.Count == 1)
        {
            // Single file: drop the "_1" ordinal.
            var only = _partPaths[0];
            var renamed = Path.Combine(_folder, $"{_baseName}_{_timestamp}.jsonl");
            if (!string.Equals(only, renamed, StringComparison.Ordinal))
            {
                if (File.Exists(renamed))
                {
                    File.Delete(renamed);
                }
                File.Move(only, renamed);
            }
            FinalPaths = new[] { renamed };
        }
        else
        {
            FinalPaths = _partPaths.ToArray();
        }
    }
}
