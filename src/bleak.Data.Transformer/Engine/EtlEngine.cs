using System.Text.Json;
using bleak.Data.InfiniteDataLoader;
using bleak.Data.Transformer.Config;
using bleak.Data.Transformer.Join;
using bleak.Data.Transformer.Output;
using bleak.Data.Transformer.Transforms;

namespace bleak.Data.Transformer.Engine;

/// <summary>The per-output outcome of a run.</summary>
public sealed class EtlOutputResult
{
    public required string Name { get; init; }
    public required long Records { get; init; }
    public required IReadOnlyList<string> Files { get; init; }
    public required long Rejected { get; init; }
    public long Filtered { get; init; }

    /// <summary>Records that parsed and were kept, but had a field flagged as worth reporting upstream.</summary>
    public long Warnings { get; init; }
}

/// <summary>
/// Runs an ETL config: for each output it compiles a plan, joins/groups via the configured strategy,
/// maps each record to JSON, and writes JSONL (split into N-record files). The single public entry point
/// for the config-driven pipeline.
/// </summary>
public static class EtlEngine
{
    /// <summary>Placeholder a config's <c>load.folder</c> can embed to control where the per-run subfolder lands.</summary>
    private const string RunTimestampToken = "{run_timestamp}";

    public static IReadOnlyList<EtlOutputResult> Run(
        EtlConfig config,
        string configDirectory,
        string outputFolder,
        string timestamp,
        Action<string>? log = null)
    {
        var registry = TransformRegistry.CreateDefault();
        var compiler = new EtlCompiler(config, configDirectory, registry, log);
        var results = new List<EtlOutputResult>();
        // Shared across every output in this run: an identical lookup (same resolved source file, join
        // column, projected column/expr, filter, mode) compiled by a later output reuses the index a
        // prior output already built, instead of re-scanning that source from disk again. See
        // LookupPlan.CacheKey.
        var lookupIndexCache = new Dictionary<string, Dictionary<string, List<string>>>();

        foreach (var outputConfig in config.Outputs)
        {
            if (config.Run.Sample is { PrimaryRows: > 0 } s)
            {
                log?.Invoke($"[SAMPLE RUN] {outputConfig.Name}: first {s.PrimaryRows:N0} '{outputConfig.Source}' row(s), "
                    + $"scoped by {s.KeyColumn}; children are complete for those parents.");
            }
            if (config.Run.KeyFilter is { Values.Count: > 0 } kf)
            {
                log?.Invoke($"[KEY FILTER RUN] {outputConfig.Name}: '{outputConfig.Source}' scoped to {kf.Values.Count:N0} "
                    + $"explicit {kf.KeyColumn} value(s); children are complete for those parents.");
            }
            var plan = compiler.Compile(outputConfig);
            var readers = EtlCompiler.Readers(plan.Root).Concat(plan.Lookups.Select(l => l.Reader)).ToList();
            try
            {
                log?.Invoke($"--- {plan.Name} ({plan.Mode}, {config.Run.JoinStrategy}) ---");
                BuildLookupIndexes(plan.Lookups, lookupIndexCache, log);
                var baseFolder = ResolveOutputFolder(plan.Load.Folder, outputFolder, configDirectory);
                // Every run gets its own subfolder so successive runs never comingle files (jsonl, rejects,
                // filtered, warnings, sampling) — mirrors the timestamp already baked into each file name.
                // A config can place that subfolder explicitly via the "{run_timestamp}" token in its
                // "folder" (e.g. ".../output/{run_timestamp}/events" -> ".../output/run_2026.../events",
                // so every output's files land under one shared per-run folder); without the token, it's
                // appended at the end as before (".../output/events/run_2026...").
                var runFolder = baseFolder.Contains(RunTimestampToken, StringComparison.Ordinal)
                    ? baseFolder.Replace(RunTimestampToken, $"run_{timestamp}")
                    : Path.Combine(baseFolder, $"run_{timestamp}");
                log?.Invoke($"    output folder: {runFolder}");
                results.Add(RunOutput(config.Run, plan, runFolder, timestamp, log));
            }
            finally
            {
                foreach (var reader in readers)
                {
                    reader.Dispose();
                }
            }
        }

        return results;
    }

    /// <summary>Resolves the effective output folder for one feed: its <c>load.folder</c> if set, else the
    /// fallback; <c>~</c> is expanded and relative paths resolve against the config file's folder.</summary>
    private static string ResolveOutputFolder(string? loadFolder, string fallback, string configDirectory)
    {
        var raw = string.IsNullOrWhiteSpace(loadFolder) ? fallback : loadFolder;
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = Directory.GetCurrentDirectory(); // last resort; the command normally supplies a fallback
        }
        return PathUtil.Resolve(raw, configDirectory);
    }

    // TODO (engine, scalability): each lookup loads its ENTIRE source into an in-memory dictionary. That
    // is fine for small dimensions but OOMs on large lookup sources (e.g. a very large event feed whose
    // lookups over large product/itemized sources run to tens of GB). It
    // also happens regardless of joinStrategy. Plan: add a disk-backed lookup path — external-sort the
    // lookup source by its key and merge-join it against the (key-sorted) consumers, or spill the index to
    // disk — so large lookups run with bounded memory like the externalMerge relation join already does.
    /// <summary>
    /// Reads each lookup source once and indexes its projected column by the join key — unless an
    /// earlier output in this same run already built the identical index (<see cref="LookupPlan.CacheKey"/>),
    /// in which case that result is reused instead of re-scanning the source from disk.
    /// </summary>
    private static void BuildLookupIndexes(
        IReadOnlyList<LookupPlan> lookups, Dictionary<string, Dictionary<string, List<string>>> cache, Action<string>? log)
    {
        foreach (var lp in lookups)
        {
            if (lp.CacheKey is { } cacheKey && cache.TryGetValue(cacheKey, out var shared))
            {
                lp.Index = shared;
                log?.Invoke("    lookup reused from an earlier output in this run (identical source/join/projection) — skipped re-scanning it.");
                continue;
            }

            var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var skippedReasons = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in lp.Reader.ReadRows())
            {
                if (lp.Filter is not null && !lp.Filter.Passes(lp.Schema, row))
                {
                    continue;
                }
                var key = lp.Schema.RawValue(row, lp.ChildKeyColumn) ?? "";
                if (lp.IsExists)
                {
                    // Presence in the index is the whole answer; the value list content is unused.
                    index[key] = index.TryGetValue(key, out var existing) ? existing : new List<string> { "1" };
                    continue;
                }
                string? value;
                if (lp.ExprSteps is { } exprSteps)
                {
                    try
                    {
                        var start = lp.ExprFromColumn is { Length: > 0 } fc ? lp.Schema.TypedValue(row, fc) : null;
                        value = ValueConvert.To(Pipeline.Execute(exprSteps, start, lp.Schema.Context(row)), DataType.String) as string;
                    }
                    catch (Exception ex)
                    {
                        // A row this lookup's expr can't handle (e.g. an unmapped code with no "map" default)
                        // just contributes nothing — it must not take down the whole run over one bad row.
                        skippedReasons.Add(ex.Message);
                        continue;
                    }
                }
                else
                {
                    value = lp.Schema.RawValue(row, lp.Column);
                }
                if (string.IsNullOrEmpty(value))
                {
                    continue; // only index present values
                }
                if (!index.TryGetValue(key, out var list))
                {
                    list = new List<string>();
                    index[key] = list;
                }
                list.Add(value);
            }
            if (skippedReasons.Count > 0)
            {
                log?.Invoke($"    [warn] lookup on '{lp.ChildKeyColumn}': skipped rows the projection couldn't compute — {string.Join("; ", skippedReasons)}");
            }
            lp.Index = index;
            if (lp.CacheKey is { } newKey)
            {
                cache[newKey] = index;
            }
        }
    }

    private static EtlOutputResult RunOutput(
        RunConfig run, OutputPlan plan, string outputFolder, string timestamp, Action<string>? log)
    {
        IJoinStrategy strategy = run.JoinStrategy == JoinStrategyKind.ExternalMerge
            ? new ExternalMergeJoinStrategy(log: m => log?.Invoke("    " + m))
            : new HashInMemoryJoinStrategy();

        var recordWriter = new RecordWriter(run.OnError, run.EmitEmpty);
        var writer = new JsonlOutputWriter(outputFolder, plan.Load.FileBaseName, timestamp, plan.Load.SplitEvery);
        var maxRecords = run.MaxRecords is > 0 ? run.MaxRecords.Value : long.MaxValue;
        var capped = false;
        // Object-mode outputs only: a running reservoir sample, written out as individual pretty JSON
        // files at the end so someone can eyeball a handful of records without opening a multi-GB jsonl.
        var sampler = plan.Mode == OutputMode.Object && plan.Load.Sampling is { Enabled: true, Count: > 0 } sc
            ? new ReservoirSampler(sc.Count)
            : null;
        // A separate, non-random top-K sample: the most fully-populated records (most non-empty nested
        // relations), for eyeballing how the schema looks when every optional structure is present.
        var richSampler = plan.Mode == OutputMode.Object && plan.Load.RichSampling is { Enabled: true, Count: > 0 } rsc
            ? new TopKSampler(rsc.Count)
            : null;
        using var rejects = new RejectSink(outputFolder, plan.Load.FileBaseName, timestamp);
        using var filtered = new FilteredSink(outputFolder, plan.Load.FileBaseName, timestamp);
        using var warnings = new WarningSink(outputFolder, plan.Load.FileBaseName, timestamp);
        long rejectedCount = 0, filteredCount = 0, warningCount = 0;

        void DrainWarnings()
        {
            foreach (var fw in recordWriter.DrainWarnings())
            {
                warningCount++;
                warnings.Write(fw);
            }
        }

        // Runs the requireRelations check, discarding any key warnings it raised: an emitted record
        // re-raises them when rendered, and a skipped one shouldn't report them at all.
        bool RequireCheck(Func<bool> check)
        {
            var result = check();
            recordWriter.DrainWarnings();
            return result;
        }

        // Returns true if the primary row passes the filter; otherwise counts it (and optionally records it).
        bool Keep(string[] row)
        {
            if (plan.Filter is null || plan.Filter.Passes(plan.Root.Schema, row))
            {
                return true;
            }
            filteredCount++;
            if (plan.FilterExcludedFile)
            {
                filtered.Write(row);
            }
            return false;
        }

        try
        {
            if (plan.Mode == OutputMode.KeyedMap)
            {
                foreach (var group in strategy.Group(plan.Root, plan.GroupByColumn!))
                {
                    if (writer.RecordsWritten >= maxRecords)
                    {
                        capped = true;
                        break; // run.maxRecords reached; disposing the enumerator releases the sort temp files
                    }
                    var kept = plan.Filter is null ? group : group.Where(Keep).ToList();
                    if (kept.Count == 0)
                    {
                        continue;
                    }
                    try
                    {
                        writer.WriteRecord(w => recordWriter.WriteKeyedMap(w, plan, kept));
                        DrainWarnings();
                    }
                    catch (RowSkipException ex)
                    {
                        rejectedCount++;
                        rejects.Write(kept[0], ex.Message);
                    }
                }
            }
            else
            {
                var seenDistinctBy = plan.DistinctByColumn is { Length: > 0 }
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : null;

                foreach (var joined in strategy.Join(plan.Root))
                {
                    if (writer.RecordsWritten >= maxRecords)
                    {
                        capped = true;
                        break; // run.maxRecords reached; disposing the enumerator releases the sort temp files
                    }
                    if (!Keep(joined.Row))
                    {
                        continue;
                    }
                    if (plan.RequireRelations is { } required
                        && !RequireCheck(() => recordWriter.HasAnyRelationContent(plan.Root, joined, required)))
                    {
                        filteredCount++;
                        if (plan.FilterExcludedFile)
                        {
                            filtered.Write(joined.Row);
                        }
                        continue; // none of the required relations has content for this record
                    }
                    if (seenDistinctBy is not null
                        && !seenDistinctBy.Add(plan.Root.Schema.RawValue(joined.Row, plan.DistinctByColumn!) ?? ""))
                    {
                        continue; // another row with this key already became the kept primary
                    }
                    try
                    {
                        writer.WriteRecord(w => recordWriter.WriteObject(w, plan.Root, joined));
                        DrainWarnings();
                        if (sampler is not null && sampler.ShouldTake(out var slot))
                        {
                            sampler.Set(slot, RenderSample(recordWriter, plan.Root, joined, plan.Load.Sampling.FileNameFields));
                            recordWriter.DrainWarnings(); // discard: duplicate of what we already recorded above
                        }
                        if (richSampler is not null)
                        {
                            var score = RecordWriter.ComputeRichnessScore(plan.Root, joined);
                            if (richSampler.ShouldTake(score, out var richSlot))
                            {
                                richSampler.Set(richSlot, score, RenderSample(recordWriter, plan.Root, joined, plan.Load.RichSampling.FileNameFields));
                                recordWriter.DrainWarnings(); // discard: duplicate of what we already recorded above
                            }
                        }
                    }
                    catch (RowSkipException ex)
                    {
                        rejectedCount++;
                        rejects.Write(joined.Row, ex.Message);
                    }
                }
            }
        }
        finally
        {
            writer.Dispose(); // flush + finalize file paths (even if a later step aborts)
        }

        if (sampler is { Count: > 0 })
        {
            WriteSamples(sampler.Items(), outputFolder, plan.Load.Sampling.Folder, log);
        }
        if (richSampler is { Count: > 0 })
        {
            WriteSamples(richSampler.Items(), outputFolder, plan.Load.RichSampling.Folder, log, label: "rich sampling");
        }

        if (capped)
        {
            log?.Invoke($"    stopped at run.maxRecords = {maxRecords:N0} record(s); the feed is incomplete.");
        }
        if (rejectedCount > 0)
        {
            log?.Invoke($"    {rejectedCount:N0} record(s) skipped -> {rejects.Path}");
        }
        if (filteredCount > 0)
        {
            log?.Invoke($"    {filteredCount:N0} record(s) filtered out" + (filtered.Path is { } p ? $" -> {p}" : ""));
        }
        if (warningCount > 0)
        {
            log?.Invoke($"    {warningCount:N0} warning(s) (record kept) -> {warnings.Path}");
        }

        return new EtlOutputResult
        {
            Name = plan.Name,
            Records = writer.RecordsWritten,
            Files = writer.FinalPaths,
            Rejected = rejectedCount,
            Filtered = filteredCount,
            Warnings = warningCount,
        };
    }

    /// <summary>Re-renders one record (cheap: only happens for the handful of rows the reservoir keeps) as
    /// indented JSON, and derives its sample file name from <paramref name="fileNameFields"/>.</summary>
    private static SampleRecord RenderSample(RecordWriter recordWriter, NodePlan root, JoinedRow joined, List<string> fileNameFields)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            recordWriter.WriteObject(w, root, joined);
        }
        var json = System.Text.Encoding.UTF8.GetString(ms.ToArray());

        using var doc = JsonDocument.Parse(json);
        var parts = new List<string>();
        foreach (var field in fileNameFields)
        {
            if (!doc.RootElement.TryGetProperty(field, out var v))
            {
                continue;
            }
            var s = v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };
            if (!string.IsNullOrEmpty(s))
            {
                parts.Add(s);
            }
        }
        if (parts.Count == 0)
        {
            parts.Add(Guid.NewGuid().ToString("N"));
        }

        return new SampleRecord(json, string.Join("_", parts.Select(SanitizeFileNamePart)));
    }

    private static string SanitizeFileNamePart(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => invalid.Contains(c) ? '-' : c).ToArray());
    }

    private static void WriteSamples(IEnumerable<SampleRecord> samples, string outputFolder, string samplingFolder, Action<string>? log, string label = "sampling")
    {
        var dir = Path.Combine(outputFolder, samplingFolder);
        Directory.CreateDirectory(dir);
        var written = 0;
        foreach (var sample in samples)
        {
            var path = UniquePath(Path.Combine(dir, sample.FileNameBase + ".json"));
            File.WriteAllText(path, sample.Json);
            written++;
        }
        log?.Invoke($"    {label}: wrote {written:N0} record(s) to {dir}");
    }

    /// <summary>Appends "_2", "_3", ... before the extension if the path is already taken (e.g. two
    /// customers sharing the same first/last name landed in the same reservoir).</summary>
    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name}_{i}{ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}

/// <summary>A rendered sample record awaiting write-out: its pretty JSON text and derived file name (no extension).</summary>
public readonly record struct SampleRecord(string Json, string FileNameBase);

/// <summary>
/// Classic reservoir sampling (Algorithm R): maintains a uniform random sample of N items from a stream
/// of unknown length, seeing each item exactly once. The caller calls <see cref="ShouldTake"/> for every
/// candidate; when it returns true, the caller does the (comparatively expensive) work of producing the
/// item and passes it to <see cref="Set"/>. Not thread-safe.
/// </summary>
public sealed class ReservoirSampler
{
    private readonly Random _rng = new();
    private readonly SampleRecord?[] _slots;
    private long _seen;

    public ReservoirSampler(int count) => _slots = new SampleRecord?[count];

    public int Count => _slots.Count(s => s is not null);

    /// <summary>Call once per candidate record. Returns true (with the slot to fill) roughly Count/seen of the time.</summary>
    public bool ShouldTake(out int slot)
    {
        var i = _seen++;
        if (i < _slots.Length)
        {
            slot = (int)i;
            return true;
        }
        var r = _rng.NextInt64(0, i + 1);
        if (r < _slots.Length)
        {
            slot = (int)r;
            return true;
        }
        slot = -1;
        return false;
    }

    public void Set(int slot, SampleRecord record) => _slots[slot] = record;

    public IEnumerable<SampleRecord> Items() => _slots.Where(s => s is not null).Select(s => s!.Value);
}

/// <summary>
/// Keeps the N candidates with the highest score seen so far (a small top-K, not a uniform sample) —
/// used for <see cref="LoadConfig.RichSampling"/> to surface the most fully-populated records (most
/// non-empty nested relations, e.g. payment_cards/household_member) rather than random ones.
/// The caller computes each candidate's (cheap) score first via <see cref="RecordWriter.ComputeRichnessScore"/>;
/// only when <see cref="ShouldTake"/> says the candidate would make the current top-K does it do the
/// (comparatively expensive) work of rendering the record and passing it to <see cref="Set"/>. Not thread-safe.
/// </summary>
public sealed class TopKSampler
{
    private readonly (int Score, SampleRecord? Record)[] _slots;
    private int _filled;

    public TopKSampler(int count) => _slots = new (int, SampleRecord?)[count];

    public int Count => _filled;

    /// <summary>Call once per candidate with its richness score. Returns true (with the slot to fill) when
    /// the top-K isn't full yet, or this score beats the current worst entry in it.</summary>
    public bool ShouldTake(int score, out int slot)
    {
        if (_filled < _slots.Length)
        {
            slot = _filled;
            return true;
        }
        var worst = 0;
        for (var i = 1; i < _slots.Length; i++)
        {
            if (_slots[i].Score < _slots[worst].Score)
            {
                worst = i;
            }
        }
        if (score > _slots[worst].Score)
        {
            slot = worst;
            return true;
        }
        slot = -1;
        return false;
    }

    public void Set(int slot, int score, SampleRecord record)
    {
        if (slot == _filled && _filled < _slots.Length)
        {
            _filled++;
        }
        _slots[slot] = (score, record);
    }

    public IEnumerable<SampleRecord> Items() => _slots.Take(_filled).Select(s => s.Record!.Value);
}

/// <summary>Lazily-created sink for filter-excluded rows: writes each excluded primary row as a CSV line.</summary>
public sealed class FilteredSink : IDisposable
{
    private readonly string _folder;
    private readonly string _baseName;
    private readonly string _timestamp;
    private StreamWriter? _writer;

    public FilteredSink(string folder, string baseName, string timestamp)
    {
        _folder = folder;
        _baseName = baseName;
        _timestamp = timestamp;
    }

    public string? Path { get; private set; }

    public void Write(string[] row)
    {
        if (_writer is null)
        {
            Path = System.IO.Path.Combine(_folder, $"{_baseName}_{_timestamp}_filtered.csv");
            _writer = new StreamWriter(Path, append: false) { NewLine = "\n" };
        }
        _writer.WriteLine(CsvWriter.FormatRecord(row));
    }

    public void Dispose() => _writer?.Dispose();
}

/// <summary>Lazily-created sink for skipped rows: writes the offending primary row as a CSV line.</summary>
public sealed class RejectSink : IDisposable
{
    private readonly string _folder;
    private readonly string _baseName;
    private readonly string _timestamp;
    private StreamWriter? _writer;

    public RejectSink(string folder, string baseName, string timestamp)
    {
        _folder = folder;
        _baseName = baseName;
        _timestamp = timestamp;
    }

    public string? Path { get; private set; }

    public void Write(string[] row, string reason)
    {
        if (_writer is null)
        {
            Path = System.IO.Path.Combine(_folder, $"{_baseName}_{_timestamp}_rejects.csv");
            _writer = new StreamWriter(Path, append: false) { NewLine = "\n" };
            _writer.WriteLine("# reason\trow");
        }
        _writer.WriteLine($"# {reason}");
        _writer.WriteLine(CsvWriter.FormatRecord(row));
    }

    public void Dispose() => _writer?.Dispose();
}

/// <summary>
/// Lazily-created sink for <see cref="FieldWarning"/>s: rows that parsed fine but whose raw form was
/// unusual enough to report back to the source system's owners. Unlike <see cref="RejectSink"/>, a
/// warning never drops the record — the row still made it into the main jsonl output.
/// </summary>
public sealed class WarningSink : IDisposable
{
    private readonly string _folder;
    private readonly string _baseName;
    private readonly string _timestamp;
    private StreamWriter? _writer;

    public WarningSink(string folder, string baseName, string timestamp)
    {
        _folder = folder;
        _baseName = baseName;
        _timestamp = timestamp;
    }

    public string? Path { get; private set; }

    public void Write(FieldWarning w)
    {
        if (_writer is null)
        {
            Path = System.IO.Path.Combine(_folder, $"{_baseName}_{_timestamp}_warnings.csv");
            _writer = new StreamWriter(Path, append: false) { NewLine = "\n" };
            _writer.WriteLine("record_key\tfield\treason\traw_value");
        }
        _writer.WriteLine($"{w.Key}\t{w.Field}\t{w.Reason}\t{w.RawValue}");
    }

    public void Dispose() => _writer?.Dispose();
}
