using bleak.Data.InfiniteDataLoader;

namespace bleak.Data.Transformer.Sources;

/// <summary>An <see cref="ISourceReader"/> backed by the streaming, RFC-4180-aware <see cref="CsvReader"/>.</summary>
public sealed class DelimitedSourceReader : ISourceReader
{
    private readonly CsvReader _reader;

    public DelimitedSourceReader(string path, char delimiter, char quote)
    {
        _reader = new CsvReader(path, delimiter, quote);
    }

    public IReadOnlyList<string> Headers => _reader.Headers;
    public int IndexOf(string column) => _reader.IndexOf(column);
    public IEnumerable<string[]> ReadRows() => _reader.ReadRecords();
    public void Dispose() => _reader.Dispose();
}
