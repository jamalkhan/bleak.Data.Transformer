using bleak.Data.Transformer.Config;
using bleak.Data.Transformer.Transforms;

namespace bleak.Data.Transformer.Sources;

/// <summary>
/// A forward-only reader over a tabular source. Rows are delivered as <c>string[]</c> (raw cells) so the
/// existing streaming sort/merge primitives can operate on them; typing is applied on access via
/// <see cref="SourceSchema"/>. Nested formats (JSON/XML) will extend this contract later.
/// </summary>
public interface ISourceReader : IDisposable
{
    IReadOnlyList<string> Headers { get; }
    int IndexOf(string column);
    IEnumerable<string[]> ReadRows();
}

/// <summary>
/// Wraps a reader so it yields at most <c>maxRows</c> rows. Used for the PRIMARY source of a sampled run
/// (<see cref="SampleConfig.PrimaryRows"/>): the file is only read as far as the cap, so the external sort
/// in front of the join — which drains its whole input before yielding — has almost nothing to do.
/// </summary>
public sealed class LimitedSourceReader : ISourceReader
{
    private readonly ISourceReader _inner;
    private readonly int _maxRows;

    public LimitedSourceReader(ISourceReader inner, int maxRows)
    {
        if (maxRows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRows), "maxRows must be greater than zero.");
        }
        _inner = inner;
        _maxRows = maxRows;
    }

    public IReadOnlyList<string> Headers => _inner.Headers;
    public int IndexOf(string column) => _inner.IndexOf(column);
    public IEnumerable<string[]> ReadRows() => _inner.ReadRows().Take(_maxRows);
    public void Dispose() => _inner.Dispose();
}

/// <summary>
/// Wraps a non-primary source so it yields only the rows belonging to the sampled parents: a streaming
/// semi-join against the primary's key set. The whole file is still scanned (matches can be anywhere), but
/// only surviving rows reach the sort/nest stages, so a sampled run keeps complete children at every depth
/// without the full feed's sort and memory cost.
/// </summary>
public sealed class KeyFilteredSourceReader : ISourceReader
{
    private readonly ISourceReader _inner;
    private readonly int _keyIndex;
    private readonly HashSet<string> _keys;

    public KeyFilteredSourceReader(ISourceReader inner, int keyIndex, HashSet<string> keys)
    {
        _inner = inner;
        _keyIndex = keyIndex;
        _keys = keys;
    }

    /// <summary>Rows kept by the filter (populated as the source is read).</summary>
    public long RowsKept { get; private set; }

    /// <summary>Rows scanned, including those the filter dropped.</summary>
    public long RowsScanned { get; private set; }

    public IReadOnlyList<string> Headers => _inner.Headers;
    public int IndexOf(string column) => _inner.IndexOf(column);

    public IEnumerable<string[]> ReadRows()
    {
        foreach (var row in _inner.ReadRows())
        {
            RowsScanned++;
            // Compare the raw cell, exactly as JoinKeys.Composite does, so membership matches the join.
            if (_keyIndex < row.Length && _keys.Contains(row[_keyIndex]))
            {
                RowsKept++;
                yield return row;
            }
        }
    }

    public void Dispose() => _inner.Dispose();
}

/// <summary>Builds an <see cref="ISourceReader"/> for a configured source.</summary>
public static class SourceReaderFactory
{
    public static ISourceReader Open(SourceConfig source, string baseDirectory)
    {
        if (!string.Equals(source.Format, "delimited", StringComparison.OrdinalIgnoreCase))
        {
            throw new EtlConfigException($"source format '{source.Format}' is not supported yet (only 'delimited').");
        }

        var path = PathUtil.Resolve(source.File, baseDirectory);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"source file not found: {path}", path);
        }

        // Transparently decrypts/decompresses a "*.csv.gz.pgp" / "*.csv.gz" source into a plain CSV
        // first; a no-op for every source that's already plain. See EncryptedSourceCache.
        path = EncryptedSourceCache.ResolvePlainPath(path, source.Decrypt, baseDirectory);

        return new DelimitedSourceReader(path, ResolveDelimiter(source.Delimiter), ResolveChar(source.Quote, '"'));
    }

    /// <summary>Resolves a delimiter shorthand or literal character.</summary>
    public static char ResolveDelimiter(string delimiter) => delimiter?.ToLowerInvariant() switch
    {
        null or "" or "," or "csv" or "comma" => ',',
        "\t" or "tsv" or "tab" => '\t',
        "|" or "pipe" => '|',
        ";" or "semicolon" => ';',
        _ when delimiter.Length == 1 => delimiter[0],
        _ => throw new EtlConfigException($"unrecognized delimiter '{delimiter}'."),
    };

    private static char ResolveChar(string? s, char fallback) => string.IsNullOrEmpty(s) ? fallback : s[0];
}
