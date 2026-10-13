using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace bleak.Data.Transformer.Output;

/// <summary>
/// A sink for output records. v1 renders each record as JSON via a <see cref="Utf8JsonWriter"/>; the
/// interface is the seam where delimited/XML/other writers plug in later.
/// </summary>
public interface IOutputWriter : IDisposable
{
    /// <summary>Renders and persists one record. The callback writes the record body to the writer.</summary>
    void WriteRecord(Action<Utf8JsonWriter> writeJson);

    long RecordsWritten { get; }

    /// <summary>Final output paths; valid after <see cref="IDisposable.Dispose"/>.</summary>
    IReadOnlyList<string> FinalPaths { get; }
}

/// <summary>Writes one JSON document per line (JSONL), rolling files every N records. Reuses a single
/// buffer + <see cref="Utf8JsonWriter"/> across records so it never materializes the whole feed.</summary>
public sealed class JsonlOutputWriter : IOutputWriter
{
    /// <summary>Compact output with relaxed escaping (so <c>@</c>, <c>+</c> etc. are written literally).</summary>
    public static readonly JsonWriterOptions JsonOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly JsonlSplitWriter _split;
    private readonly ArrayBufferWriter<byte> _buffer = new();
    private readonly Utf8JsonWriter _json;

    public JsonlOutputWriter(string folder, string baseName, string timestamp, long splitEvery)
    {
        _split = new JsonlSplitWriter(folder, baseName, timestamp, splitEvery);
        _json = new Utf8JsonWriter(_buffer, JsonOptions);
    }

    public long RecordsWritten => _split.RecordsWritten;
    public IReadOnlyList<string> FinalPaths => _split.FinalPaths;

    public void WriteRecord(Action<Utf8JsonWriter> writeJson)
    {
        _buffer.ResetWrittenCount();
        _json.Reset();
        writeJson(_json);
        _json.Flush();
        _split.WriteLine(_buffer.WrittenSpan);
    }

    public void Dispose()
    {
        _json.Dispose();
        _split.Dispose();
    }
}
