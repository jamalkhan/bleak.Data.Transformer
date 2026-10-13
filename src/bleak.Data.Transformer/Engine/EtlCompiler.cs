using System.Text.Json;
using bleak.Data.Transformer.Config;
using bleak.Data.Transformer.Sources;
using bleak.Data.Transformer.Transforms;

namespace bleak.Data.Transformer.Engine;

/// <summary>
/// Validates an <see cref="EtlConfig"/> and compiles each output into an executable <see cref="OutputPlan"/>:
/// opens source/lookup readers, resolves schemas, compiles field pipelines + the render tree, and checks
/// join/group columns. Throws <see cref="EtlConfigException"/> on any structural problem (fail fast).
/// </summary>
public sealed class EtlCompiler
{
    private readonly EtlConfig _config;
    private readonly string _baseDirectory;
    private readonly TransformRegistry _registry;
    private readonly Action<string>? _log;
    private List<LookupPlan> _lookups = new();

    /// <summary>Sampled or key-filtered runs only: the primary's key values, used to scope every other
    /// source — populated either from a <see cref="SampleConfig"/> pre-pass or directly from a
    /// <see cref="KeyFilterConfig"/>'s explicit values.</summary>
    private HashSet<string>? _sampleKeys;

    /// <summary>The column name <see cref="_sampleKeys"/> was collected against (whichever of Sample's or
    /// KeyFilter's KeyColumn is active for this output) — see <see cref="ApplySampleFilter"/>.</summary>
    private string? _sampleKeyColumn;

    public EtlCompiler(EtlConfig config, string baseDirectory, TransformRegistry registry, Action<string>? log = null)
    {
        _config = config;
        _baseDirectory = baseDirectory;
        _registry = registry;
        _log = log;

        if (config.Run.Sample is { PrimaryRows: > 0 } && config.Run.KeyFilter is { Values.Count: > 0 })
        {
            throw new EtlConfigException("run.sample and run.keyFilter are mutually exclusive.");
        }
    }

    public OutputPlan Compile(OutputConfig output)
    {
        if (string.IsNullOrWhiteSpace(output.Name))
        {
            throw new EtlConfigException("an output is missing 'name'.");
        }

        _lookups = new List<LookupPlan>();
        _sampleKeys = null; // each output samples/filters against its own primary
        _sampleKeyColumn = null;
        var root = CompileNode(output.Source, output.Fields, output.Children, isRoot: true);
        var filter = output.Filter is { Count: > 0 } ? RowFilter.Compile(output.Filter, root.Schema) : null;

        var keyedCount = CountKeyedRelations(root);
        _log?.Invoke($"    output '{output.Name}': {_lookups.Count} lookup(s) "
            + $"({_lookups.Count(l => l.IsExists)} exists-mode), {keyedCount} keyed relation group(s) compiled.");

        // The primary record's key for warnings: the keyedMap groupBy, else the join key towards its children.
        root.RecordKeyColumns = output.Mode == OutputMode.KeyedMap && !string.IsNullOrEmpty(output.GroupBy)
            ? new[] { output.GroupBy }
            : root.Children.FirstOrDefault()?.ParentKeyColumns ?? Array.Empty<string>();

        if (output.Mode == OutputMode.KeyedMap)
        {
            if (string.IsNullOrEmpty(output.GroupBy))
            {
                throw new EtlConfigException($"output '{output.Name}': keyedMap mode requires 'groupBy'.");
            }
            if (root.Schema.ColumnIndex(output.GroupBy) < 0)
            {
                throw new EtlConfigException($"output '{output.Name}': groupBy column '{output.GroupBy}' not found in source '{output.Source}'.");
            }
            if (output.RequireRelations is { Count: > 0 })
            {
                throw new EtlConfigException($"output '{output.Name}': requireRelations is only supported in object mode.");
            }
            if (output.KeyExpr is null || output.KeyExpr.Pipeline.Count == 0)
            {
                throw new EtlConfigException($"output '{output.Name}': keyedMap mode requires a 'keyExpr' pipeline.");
            }

            return new OutputPlan
            {
                Name = output.Name,
                Mode = OutputMode.KeyedMap,
                Root = root,
                Load = output.Load,
                Lookups = _lookups,
                GroupByColumn = output.GroupBy,
                KeyExprSteps = _registry.CompilePipeline(output.KeyExpr.Pipeline),
                Dedup = output.Dedup,
                Filter = filter,
                FilterExcludedFile = output.FilterExcludedFile,
            };
        }

        if (output.DistinctBy is { Length: > 0 } db && root.Schema.ColumnIndex(db) < 0)
        {
            throw new EtlConfigException($"output '{output.Name}': distinctBy column '{db}' not found in source '{output.Source}'.");
        }
        foreach (var name in output.RequireRelations ?? new List<string>())
        {
            if (RecordWriter.FindRelationItem(root.RenderItems, name) is null)
            {
                throw new EtlConfigException($"output '{output.Name}': requireRelations references '{name}', which is not a relation on the primary node.");
            }
        }

        return new OutputPlan
        {
            Name = output.Name,
            Mode = OutputMode.Object,
            Root = root,
            Load = output.Load,
            Lookups = _lookups,
            Filter = filter,
            FilterExcludedFile = output.FilterExcludedFile,
            DistinctByColumn = output.DistinctBy,
            RequireRelations = output.RequireRelations is { Count: > 0 } rr ? rr : null,
        };
    }

    private NodePlan CompileNode(string alias, List<FieldConfig> fields, List<ChildConfig> nodeChildren, bool isRoot = false)
    {
        var sourceConfig = ResolveSource(alias);
        var opened = SourceReaderFactory.Open(sourceConfig, _baseDirectory);
        var schema = new SourceSchema(opened.Headers, sourceConfig);

        ISourceReader reader = opened;
        if (isRoot && _config.Run.Sample is { PrimaryRows: > 0 } sample)
        {
            // Cap the primary, then collect its keys so every other source can be scoped to those parents.
            reader = new LimitedSourceReader(opened, sample.PrimaryRows);
            _sampleKeyColumn = sample.KeyColumn;
            _sampleKeys = CollectSampleKeys(alias, sourceConfig, schema, sample);
        }
        else if (isRoot && _config.Run.KeyFilter is { Values.Count: > 0 } keyFilter)
        {
            // Unlike Sample, the key set is given explicitly — no pre-pass needed. The primary itself is
            // also filtered by value (not just capped by count), same as every other source.
            if (string.IsNullOrWhiteSpace(keyFilter.KeyColumn))
            {
                throw new EtlConfigException("run.keyFilter requires 'keyColumn' (the column that scopes a row to a parent).");
            }
            if (schema.ColumnIndex(keyFilter.KeyColumn) < 0)
            {
                throw new EtlConfigException($"run.keyFilter: keyColumn '{keyFilter.KeyColumn}' not found in primary source '{alias}'.");
            }
            _sampleKeyColumn = keyFilter.KeyColumn;
            _sampleKeys = new HashSet<string>(keyFilter.Values, StringComparer.Ordinal);
            _log?.Invoke($"    keyFilter: scoping to {_sampleKeys.Count:N0} explicit {keyFilter.KeyColumn} value(s): "
                + string.Join(", ", keyFilter.Values));
            reader = ApplySampleFilter(alias, opened, schema);
        }
        else if (!isRoot)
        {
            reader = ApplySampleFilter(alias, opened, schema);
        }

        var relations = new List<RelationPlan>();
        var renderItems = CompileItems(fields, schema, relations);

        // Node-level relationships render after the fields; relations sharing an 'as' are concatenated.
        var nodeRels = nodeChildren.Select(c => CompileRelation(c, schema)).ToList();
        relations.AddRange(nodeRels);
        renderItems.AddRange(GroupRelationsByName(nodeChildren, nodeRels));

        ValidateKeysOfTargets(renderItems, renderItems);

        return new NodePlan
        {
            SourceAlias = alias,
            Reader = reader,
            Schema = schema,
            Children = relations,
            RenderItems = renderItems,
        };
    }

    /// <summary>
    /// Pre-pass over the primary source: reads its first <c>sample.PrimaryRows</c> rows with a throwaway
    /// reader (the real one is forward-only and single-use) and collects the distinct raw key values.
    /// </summary>
    private HashSet<string> CollectSampleKeys(string alias, SourceConfig sourceConfig, SourceSchema schema, SampleConfig sample)
    {
        if (string.IsNullOrWhiteSpace(sample.KeyColumn))
        {
            throw new EtlConfigException("run.sample requires 'keyColumn' (the column that scopes a row to a parent).");
        }
        var keyIndex = schema.ColumnIndex(sample.KeyColumn);
        if (keyIndex < 0)
        {
            throw new EtlConfigException(
                $"run.sample: keyColumn '{sample.KeyColumn}' not found in primary source '{alias}'.");
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var rows = 0;
        using var probe = SourceReaderFactory.Open(sourceConfig, _baseDirectory);
        foreach (var row in probe.ReadRows())
        {
            if (++rows > sample.PrimaryRows)
            {
                break;
            }
            if (keyIndex < row.Length && row[keyIndex].Length > 0)
            {
                keys.Add(row[keyIndex]);
            }
        }

        _log?.Invoke($"    sample: {Math.Min(rows, sample.PrimaryRows):N0} primary row(s) from '{alias}' "
            + $"-> {keys.Count:N0} distinct {sample.KeyColumn} value(s); other sources are scoped to those parents.");
        return keys;
    }

    /// <summary>Scopes a non-primary source to the sampled parents, or reads it in full if it lacks the key column.</summary>
    private ISourceReader ApplySampleFilter(string alias, ISourceReader reader, SourceSchema schema)
    {
        if (_sampleKeys is null)
        {
            return reader; // not a sampled run
        }

        var keyColumn = _sampleKeyColumn!;
        var keyIndex = schema.ColumnIndex(keyColumn);
        if (keyIndex < 0)
        {
            _log?.Invoke($"    [warn] sample: source '{alias}' has no '{keyColumn}' column; reading it in full.");
            return reader;
        }
        return new KeyFilteredSourceReader(reader, keyIndex, _sampleKeys);
    }

    /// <summary>Compiles a list of field configs into render items, collecting any relationships into <paramref name="relations"/>.</summary>
    private List<RenderItem> CompileItems(List<FieldConfig> fields, SourceSchema nodeSchema, List<RelationPlan> relations)
    {
        var items = new List<RenderItem>();
        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name))
            {
                throw new EtlConfigException("a field is missing 'name'.");
            }

            if (field.Coalesce is { Count: > 0 })
            {
                items.Add(new FieldItem { Field = CompileCoalesceField(field, nodeSchema) });
            }
            else if (field.Lookup is not null)
            {
                items.Add(new FieldItem { Field = CompileLookupField(field, nodeSchema) });
            }
            else if (field.Source is { Length: > 0 })
            {
                // Inline relationship field: an array of objects, rendered at this position.
                var inline = new ChildConfig
                {
                    Source = field.Source,
                    As = field.Name,
                    Cardinality = Cardinality.Many,
                    On = field.On ?? new JoinKeysConfig(),
                    CaseInsensitiveJoin = field.CaseInsensitiveJoin,
                    Fields = field.Fields ?? new List<FieldConfig>(),
                    Children = field.Children ?? new List<ChildConfig>(),
                    DistinctBy = field.DistinctBy,
                    Limit = field.Limit,
                    ShareDistinct = field.ShareDistinct,
                    Filter = field.Filter,
                };
                var rel = CompileRelation(inline, nodeSchema);
                relations.Add(rel);
                // Inline fields sharing a name render as one array, concatenated in declaration order.
                var existing = items.FindIndex(i => i is RelationItem { AsArray: true, Keyed: false, Inline: false } ri && ri.Name == field.Name);
                if (existing >= 0)
                {
                    var prior = (RelationItem)items[existing];
                    items[existing] = new RelationItem { Name = field.Name, AsArray = true, Relations = prior.Relations.Append(rel).ToList() };
                }
                else
                {
                    items.Add(new RelationItem { Name = field.Name, AsArray = true, Relations = new[] { rel } });
                }
            }
            else if (field.Type == DataType.Object || field.Fields is not null)
            {
                var inner = CompileItems(field.Fields ?? new List<FieldConfig>(), nodeSchema, relations);
                if (field.Children is not null)
                {
                    var groupRels = field.Children.Select(c => CompileRelation(c, nodeSchema)).ToList();
                    relations.AddRange(groupRels);
                    inner.AddRange(GroupRelationsByName(field.Children, groupRels));
                }
                items.Add(new GroupItem { Name = field.Name, Items = inner });
            }
            else if (field.KeysOf is { Count: > 0 } keysOf)
            {
                items.Add(new FieldItem
                {
                    Field = new FieldPlan
                    {
                        Name = field.Name,
                        FromColumn = null,
                        OutputType = DataType.Array,
                        Steps = Array.Empty<Step>(),
                        OnError = ParseFieldErrorMode(field.Name, field.OnError),
                        EmitNull = field.EmitNull,
                        KeysOfRelations = keysOf,
                    },
                });
            }
            else
            {
                items.Add(new FieldItem
                {
                    Field = new FieldPlan
                    {
                        Name = field.Name,
                        FromColumn = field.From,
                        OutputType = field.Type,
                        Steps = _registry.CompilePipeline(field.Pipeline),
                        OnError = ParseFieldErrorMode(field.Name, field.OnError),
                        EmitNull = field.EmitNull,
                    },
                });
            }
        }
        return items;
    }

    /// <summary>Fails fast on a <c>keysOf</c> typo: every relation name a <c>keysOf</c> field references
    /// must exist as a KEYED relation somewhere on this same node (recursively, including inside
    /// object-groups) — the exact scope <c>RecordWriter.FindKeyedRelationItem</c> searches at render time.</summary>
    private static void ValidateKeysOfTargets(IReadOnlyList<RenderItem> items, IReadOnlyList<RenderItem> topLevel)
    {
        foreach (var item in items)
        {
            switch (item)
            {
                case FieldItem { Field.KeysOfRelations: { Count: > 0 } names } fi:
                    foreach (var name in names)
                    {
                        if (!ContainsKeyedRelation(topLevel, name))
                        {
                            throw new EtlConfigException(
                                $"field '{fi.Field.Name}': keysOf references '{name}', which is not a keyed relation on this node.");
                        }
                    }
                    break;
                case GroupItem gi:
                    ValidateKeysOfTargets(gi.Items, topLevel);
                    break;
            }
        }
    }

    private static bool ContainsKeyedRelation(IReadOnlyList<RenderItem> items, string name)
    {
        foreach (var item in items)
        {
            switch (item)
            {
                case RelationItem { Keyed: true } ri when ri.Name == name:
                    return true;
                case GroupItem gi when ContainsKeyedRelation(gi.Items, name):
                    return true;
            }
        }
        return false;
    }

    /// <summary>Groups relations by their <c>as</c> name (first-occurrence order) into one RelationItem each.</summary>
    private static List<RenderItem> GroupRelationsByName(List<ChildConfig> configs, List<RelationPlan> rels)
    {
        var order = new List<string>();
        var byName = new Dictionary<string, List<RelationPlan>>(StringComparer.Ordinal);
        for (var i = 0; i < rels.Count; i++)
        {
            var name = rels[i].As;
            if (!byName.TryGetValue(name, out var list))
            {
                list = new List<RelationPlan>();
                byName[name] = list;
                order.Add(name);
            }
            list.Add(rels[i]);
        }

        var items = new List<RenderItem>();
        foreach (var name in order)
        {
            var group = byName[name];
            var keyedCount = group.Count(r => r.KeyBySteps is not null);
            if (keyedCount > 0 && keyedCount != group.Count)
            {
                throw new EtlConfigException($"relationship '{name}': all sources sharing this 'as' must either all use 'keyBy' or none.");
            }
            if (keyedCount > 0 && group.Select(r => r.KeyedSingle).Distinct().Count() > 1)
            {
                throw new EtlConfigException($"relationship '{name}': all sources sharing this 'as' must agree on 'keyedSingle'.");
            }
            var keyed = keyedCount > 0;
            var inlineCount = group.Count(r => r.Inline);
            if (inlineCount > 0 && (inlineCount != group.Count || group.Count > 1))
            {
                throw new EtlConfigException($"relationship '{name}': 'inline' must be the only source sharing this 'as' name (nothing else to combine it with).");
            }
            var inline = inlineCount > 0;
            var asArray = group.Count > 1 || group.Any(r => r.Cardinality == Cardinality.Many);
            items.Add(new RelationItem
            {
                Name = name,
                AsArray = asArray,
                Relations = group,
                Keyed = keyed,
                KeyedSingle = keyed && group[0].KeyedSingle,
                Inline = inline,
            });
        }
        return items;
    }

    private FieldPlan CompileLookupField(FieldConfig field, SourceSchema nodeSchema)
    {
        var lc = field.Lookup!;
        var plan = BuildLookupPlan(field.Name, lc, nodeSchema);

        List<LookupPlan>? extraLookups = null;
        if (field.Lookups is { Count: > 0 })
        {
            extraLookups = field.Lookups.Select(extra => BuildLookupPlan(field.Name, extra, nodeSchema)).ToList();
        }

        return new FieldPlan
        {
            Name = field.Name,
            FromColumn = null,
            OutputType = plan.IsExists ? DataType.Bool : (plan.Single ? field.Type : DataType.Array),
            // A lookup field may also carry a pipeline (e.g. "map") to post-process its raw result.
            Steps = _registry.CompilePipeline(field.Pipeline),
            Lookup = plan,
            ExtraLookups = extraLookups,
            OnError = ParseFieldErrorMode(field.Name, field.OnError),
            EmitNull = field.EmitNull,
        };
    }

    /// <summary>
    /// Compiles a coalesce field: the first non-empty candidate wins, from an ordered list each either
    /// a column on this row or a lookup (always forced to a single scalar). See
    /// <see cref="FieldConfig.Coalesce"/>.
    /// </summary>
    private FieldPlan CompileCoalesceField(FieldConfig field, SourceSchema nodeSchema)
    {
        var emitLabel = string.Equals(field.CoalesceEmit, "label", StringComparison.OrdinalIgnoreCase);
        if (field.CoalesceEmit is { Length: > 0 } ce && !emitLabel && !string.Equals(ce, "value", StringComparison.OrdinalIgnoreCase))
        {
            throw new EtlConfigException($"field '{field.Name}': unknown coalesceEmit '{ce}' (expected 'value' or 'label').");
        }

        var candidates = new List<CoalesceCandidatePlan>();
        foreach (var c in field.Coalesce!)
        {
            var hasFrom = !string.IsNullOrWhiteSpace(c.From);
            var hasLookup = c.Lookup is not null;
            if (hasFrom == hasLookup)
            {
                throw new EtlConfigException($"field '{field.Name}': each coalesce candidate needs exactly one of 'from' or 'lookup'.");
            }
            if (emitLabel && string.IsNullOrWhiteSpace(c.Label))
            {
                throw new EtlConfigException($"field '{field.Name}': coalesceEmit 'label' requires every candidate to have a 'label'.");
            }
            if (hasFrom && nodeSchema.ColumnIndex(c.From!) < 0)
            {
                throw new EtlConfigException($"field '{field.Name}': coalesce column '{c.From}' not found on the current source.");
            }

            candidates.Add(new CoalesceCandidatePlan
            {
                FromColumn = hasFrom ? c.From : null,
                Lookup = hasLookup ? BuildLookupPlan(field.Name, c.Lookup!, nodeSchema, forceSingle: true) : null,
                Label = c.Label,
            });
        }

        return new FieldPlan
        {
            Name = field.Name,
            FromColumn = null,
            OutputType = field.Type,
            Steps = _registry.CompilePipeline(field.Pipeline),
            Coalesce = candidates,
            CoalesceEmitLabel = emitLabel,
            OnError = ParseFieldErrorMode(field.Name, field.OnError),
            EmitNull = field.EmitNull,
        };
    }

    /// <summary>Compiles a <see cref="LookupConfig"/> into a <see cref="LookupPlan"/>, registering it for index-building/disposal.</summary>
    private LookupPlan BuildLookupPlan(string fieldName, LookupConfig lc, SourceSchema nodeSchema, bool forceSingle = false)
    {
        var isExists = string.Equals(lc.Mode, "exists", StringComparison.OrdinalIgnoreCase);
        var hasExpr = lc.Expr is { Count: > 0 };
        if (string.IsNullOrWhiteSpace(lc.Source) || (!isExists && !hasExpr && string.IsNullOrWhiteSpace(lc.Column)))
        {
            throw new EtlConfigException($"field '{fieldName}': lookup requires 'source' and 'column' (or 'expr', unless mode is 'exists').");
        }
        if (string.IsNullOrWhiteSpace(lc.On.Parent) || string.IsNullOrWhiteSpace(lc.On.Child))
        {
            throw new EtlConfigException($"field '{fieldName}': lookup requires 'on.parent' and 'on.child'.");
        }
        if (nodeSchema.ColumnIndex(lc.On.Parent) < 0)
        {
            throw new EtlConfigException($"field '{fieldName}': lookup parent column '{lc.On.Parent}' not found on the current source.");
        }

        var sourceConfig = ResolveSource(lc.Source);
        var opened = SourceReaderFactory.Open(sourceConfig, _baseDirectory);
        var schema = new SourceSchema(opened.Headers, sourceConfig);
        // Lookups index their whole source in RAM, so scoping them to the sample matters for memory too.
        var reader = ApplySampleFilter(lc.Source, opened, schema);
        if (schema.ColumnIndex(lc.On.Child) < 0)
        {
            throw new EtlConfigException($"field '{fieldName}': lookup child column '{lc.On.Child}' not found in source '{lc.Source}'.");
        }
        var lookupColumn = lc.Column;
        IReadOnlyList<Step>? exprSteps = null;
        if (hasExpr)
        {
            exprSteps = _registry.CompilePipeline(lc.Expr!);
            if (lc.From is { Length: > 0 } ef && schema.ColumnIndex(ef) < 0)
            {
                throw new EtlConfigException($"field '{fieldName}': lookup 'from' column '{ef}' not found in source '{lc.Source}'.");
            }
        }
        else if (!isExists)
        {
            if (schema.ColumnIndex(lookupColumn) < 0)
            {
                throw new EtlConfigException($"field '{fieldName}': lookup column '{lookupColumn}' not found in source '{lc.Source}'.");
            }
        }
        else if (string.IsNullOrWhiteSpace(lookupColumn))
        {
            // exists mode doesn't project a value; index by the join key alone using any present column.
            lookupColumn = lc.On.Child;
        }

        RowFilter? filter = lc.Filter is { Count: > 0 } ? RowFilter.Compile(lc.Filter, schema) : null;

        var plan = new LookupPlan
        {
            Reader = reader,
            Schema = schema,
            ParentKeyColumn = lc.On.Parent,
            ChildKeyColumn = lc.On.Child,
            Column = lookupColumn ?? "",
            Single = lc.Single || forceSingle,
            Distinct = lc.Distinct,
            Limit = lc.Limit,
            IsExists = isExists,
            Filter = filter,
            ExprSteps = exprSteps,
            ExprFromColumn = lc.From,
            CacheKey = BuildLookupCacheKey(sourceConfig, lc, lookupColumn, isExists),
        };
        _lookups.Add(plan);
        return plan;
    }

    /// <summary>
    /// Identifies what a lookup indexes, independent of which output/field compiled it: the resolved
    /// source file, the join column on that source, what's projected (a column or an expr pipeline), the
    /// filter, and exists-vs-values mode. Two lookups with an equal key read/index the exact same data,
    /// so <c>EtlEngine.BuildLookupIndexes</c> builds it once and shares the result — see
    /// <see cref="LookupPlan.CacheKey"/>. Returns null (no sharing) during a sampled or key-filtered run:
    /// each output's scope is its own primary rows, so the same nominal lookup can legitimately see
    /// different rows across outputs.
    /// </summary>
    private string? BuildLookupCacheKey(SourceConfig sourceConfig, LookupConfig lc, string? lookupColumn, bool isExists)
    {
        if (_config.Run.Sample is { PrimaryRows: > 0 } || _config.Run.KeyFilter is { Values.Count: > 0 })
        {
            return null;
        }
        var resolvedFile = PathUtil.Resolve(sourceConfig.File, _baseDirectory);
        var filterJson = lc.Filter is { Count: > 0 } ? JsonSerializer.Serialize(lc.Filter) : "";
        var exprJson = lc.Expr is { Count: > 0 } ? JsonSerializer.Serialize(lc.Expr) : "";
        return string.Join("\u0001", resolvedFile, lc.On.Child, lookupColumn ?? "", isExists, filterJson, exprJson, lc.From ?? "");
    }

    private static FieldErrorMode ParseFieldErrorMode(string fieldName, string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return FieldErrorMode.Inherit;
        }
        if (string.Equals(raw, "warnAndNull", StringComparison.OrdinalIgnoreCase))
        {
            return FieldErrorMode.WarnAndNull;
        }
        throw new EtlConfigException($"field '{fieldName}': unknown onError value '{raw}' (expected 'warnAndNull').");
    }

    private RelationPlan CompileRelation(ChildConfig child, SourceSchema parentSchema)
    {
        if (string.IsNullOrWhiteSpace(child.As))
        {
            throw new EtlConfigException($"a relationship on source '{child.Source}' is missing 'as'.");
        }

        // Resolve single (parent/child) or composite (parents/children) join columns.
        var parents = child.On.Parents ?? (string.IsNullOrWhiteSpace(child.On.Parent) ? new List<string>() : new List<string> { child.On.Parent });
        var children = child.On.Children ?? (string.IsNullOrWhiteSpace(child.On.Child) ? new List<string>() : new List<string> { child.On.Child });
        if (parents.Count == 0 || children.Count == 0)
        {
            throw new EtlConfigException($"relationship '{child.As}' requires 'on.parent'/'on.child' (or 'on.parents'/'on.children').");
        }
        if (parents.Count != children.Count)
        {
            throw new EtlConfigException($"join '{child.As}': 'on.parents' and 'on.children' must have the same number of columns.");
        }
        foreach (var p in parents)
        {
            if (parentSchema.ColumnIndex(p) < 0)
            {
                throw new EtlConfigException($"join '{child.As}': parent column '{p}' not found on the parent source.");
            }
        }

        var childNode = CompileNode(child.Source, child.Fields, child.Children);
        foreach (var c in children)
        {
            if (childNode.Schema.ColumnIndex(c) < 0)
            {
                throw new EtlConfigException($"join '{child.As}': child column '{c}' not found in source '{child.Source}'.");
            }
        }
        if (child.DistinctBy is { Length: > 0 } dby && childNode.Schema.ColumnIndex(dby) < 0)
        {
            throw new EtlConfigException($"join '{child.As}': distinctBy column '{dby}' not found in source '{child.Source}'.");
        }
        if (child.MergeBy is { Length: > 0 } && child.DistinctBy is { Length: > 0 })
        {
            throw new EtlConfigException($"join '{child.As}': 'mergeBy' and 'distinctBy' are mutually exclusive.");
        }
        if (child.MergeBy is { Length: > 0 } mby && childNode.Schema.ColumnIndex(mby) < 0)
        {
            throw new EtlConfigException($"join '{child.As}': mergeBy column '{mby}' not found in source '{child.Source}'.");
        }

        // Warnings raised while rendering this child's rows report the join key as their record_key.
        childNode.RecordKeyColumns = children;

        IReadOnlyList<Step>? mergePrefixSteps = null;
        if (child.MergePrefix is { } mergePrefix)
        {
            if (mergePrefix.Pipeline.Count == 0)
            {
                throw new EtlConfigException($"join '{child.As}': mergePrefix requires a 'pipeline'.");
            }
            if (mergePrefix.From is { Length: > 0 } mf && childNode.Schema.ColumnIndex(mf) < 0)
            {
                throw new EtlConfigException($"join '{child.As}': mergePrefix.from column '{mf}' not found in source '{child.Source}'.");
            }
            mergePrefixSteps = _registry.CompilePipeline(mergePrefix.Pipeline);
        }

        IReadOnlyList<Step>? keyBySteps = null;
        if (child.KeyBy is { } keyBy)
        {
            if (keyBy.Pipeline.Count == 0)
            {
                throw new EtlConfigException($"join '{child.As}': keyBy requires a 'pipeline'.");
            }
            if (keyBy.From is { Length: > 0 } kf && childNode.Schema.ColumnIndex(kf) < 0)
            {
                throw new EtlConfigException($"join '{child.As}': keyBy.from column '{kf}' not found in source '{child.Source}'.");
            }
            keyBySteps = _registry.CompilePipeline(keyBy.Pipeline);
        }

        var relationFilter = child.Filter is { Count: > 0 } ? RowFilter.Compile(child.Filter, childNode.Schema) : null;

        if (child.Inline && child.Cardinality != Cardinality.One)
        {
            throw new EtlConfigException($"join '{child.As}': 'inline' requires cardinality 'one' (there's no flat rendering of a many-relation).");
        }
        if (child.KeyedSingleBestOf is { Length: > 0 } bestOf)
        {
            if (!child.KeyedSingle)
            {
                throw new EtlConfigException($"join '{child.As}': 'keyedSingleBestOf' requires 'keyedSingle: true'.");
            }
            if (childNode.Schema.ColumnIndex(bestOf) < 0)
            {
                throw new EtlConfigException($"join '{child.As}': keyedSingleBestOf column '{bestOf}' not found in source '{child.Source}'.");
            }
        }

        return new RelationPlan
        {
            As = child.As,
            Cardinality = child.Cardinality,
            ParentKeyColumns = parents,
            ChildKeyColumns = children,
            CaseInsensitiveJoin = child.CaseInsensitiveJoin,
            Child = childNode,
            DistinctBy = child.DistinctBy,
            MergeBy = child.MergeBy,
            MergePrefixSteps = mergePrefixSteps,
            MergePrefixFromColumn = child.MergePrefix?.From,
            Limit = child.Limit,
            ShareDistinct = child.ShareDistinct,
            Filter = relationFilter,
            KeyBySteps = keyBySteps,
            KeyByFromColumn = child.KeyBy?.From,
            KeyedSingle = child.KeyedSingle,
            KeyedSingleBestOf = child.KeyedSingleBestOf,
            Inline = child.Inline,
        };
    }

    /// <summary>Counts distinct keyed relation names in a node's render tree, recursively (incl. inside object-groups).</summary>
    private static int CountKeyedRelations(NodePlan node) => CountKeyedRelations(node.RenderItems);

    private static int CountKeyedRelations(IReadOnlyList<RenderItem> items)
    {
        var count = 0;
        foreach (var item in items)
        {
            switch (item)
            {
                case RelationItem { Keyed: true } ri:
                    count++;
                    foreach (var rel in ri.Relations)
                    {
                        count += CountKeyedRelations(rel.Child.RenderItems);
                    }
                    break;
                case RelationItem ri:
                    foreach (var rel in ri.Relations)
                    {
                        count += CountKeyedRelations(rel.Child.RenderItems);
                    }
                    break;
                case GroupItem gi:
                    count += CountKeyedRelations(gi.Items);
                    break;
            }
        }
        return count;
    }

    private SourceConfig ResolveSource(string alias)
        => _config.Sources.TryGetValue(alias, out var s)
            ? s
            : throw new EtlConfigException($"unknown source alias '{alias}'.");

    /// <summary>All relationship source readers in a plan tree (for disposal). Lookup readers are tracked separately.</summary>
    public static IEnumerable<ISourceReader> Readers(NodePlan node)
    {
        yield return node.Reader;
        foreach (var rel in node.Children)
        {
            foreach (var r in Readers(rel.Child))
            {
                yield return r;
            }
        }
    }
}
