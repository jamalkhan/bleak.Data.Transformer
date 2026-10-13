using bleak.Data.Transformer.Config;
using bleak.Data.Transformer.Transforms;

namespace bleak.Data.Transformer.Sources;

/// <summary>
/// Resolves a source's headers to column indices and applies the declared input types when a cell is
/// read. One schema is built per source (from its headers) and reused for every row.
/// </summary>
public sealed class SourceSchema
{
    private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly InputFieldConfig?[] _typesByIndex;

    public SourceSchema(IReadOnlyList<string> headers, SourceConfig config)
    {
        for (var i = 0; i < headers.Count; i++)
        {
            _index[headers[i]] = i;
        }

        _typesByIndex = new InputFieldConfig?[headers.Count];
        foreach (var (name, field) in config.Fields)
        {
            if (_index.TryGetValue(name, out var idx))
            {
                _typesByIndex[idx] = field;
            }
        }
    }

    public int ColumnIndex(string name) => _index.TryGetValue(name, out var i) ? i : -1;

    /// <summary>The raw (untyped) cell, or null when absent/empty.</summary>
    public string? RawValue(string[] row, string column)
    {
        var idx = ColumnIndex(column);
        return idx >= 0 && idx < row.Length ? row[idx] : null;
    }

    /// <summary>The typed cell value, parsed per the source's declared input type (default string).</summary>
    public object? TypedValue(string[] row, string column)
    {
        var idx = ColumnIndex(column);
        if (idx < 0 || idx >= row.Length)
        {
            return null;
        }
        var raw = row[idx];
        var spec = _typesByIndex[idx];
        return spec is null
            ? (string.IsNullOrEmpty(raw) ? null : raw)
            : ValueConvert.ParseInput(raw, spec.Type, spec.Format, spec.Culture);
    }

    public IRowContext Context(string[] row) => new RowView(this, row);

    private sealed class RowView : IRowContext
    {
        private readonly SourceSchema _schema;
        private readonly string[] _row;

        public RowView(SourceSchema schema, string[] row)
        {
            _schema = schema;
            _row = row;
        }

        public object? GetField(string column) => _schema.TypedValue(_row, column);
    }
}
