using System.Text.Json;

namespace bleak.Data.Transformer.Config;

/// <summary>
/// The deserialized ETL configuration: a pool of aliased <see cref="Sources"/> feeding any number of
/// <see cref="Outputs"/>. This is a plain DTO mirror of the JSON; it is validated and compiled into a
/// runtime plan by the engine. Flexible/polymorphic fields (operands, per-step onError) are kept as
/// <see cref="JsonElement"/> and interpreted at compile time.
/// </summary>
public sealed class EtlConfig
{
    public int Version { get; set; } = 1;
    public RunConfig Run { get; set; } = new();

    /// <summary>Alias -> source definition. Aliases are how outputs/relationships refer to files.</summary>
    public Dictionary<string, SourceConfig> Sources { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<OutputConfig> Outputs { get; set; } = new();
}

/// <summary>Run-wide settings.</summary>
public sealed class RunConfig
{
    public JoinStrategyKind JoinStrategy { get; set; } = JoinStrategyKind.HashInMemory;
    public RunErrorPolicy OnError { get; set; } = RunErrorPolicy.Skip;

    /// <summary>
    /// When false (default), fields whose value is an empty string are omitted from the output.
    /// When true, empty-string values are emitted as <c>""</c> (e.g. a blank middle_name is kept).
    /// Null values and empty arrays are always omitted regardless.
    /// </summary>
    public bool EmitEmpty { get; set; }

    /// <summary>Optional parent-scoped sample: the first N primary rows plus all of their descendants.</summary>
    public SampleConfig? Sample { get; set; }

    /// <summary>Optional parent-scoped filter to a specific, named set of key values — see
    /// <see cref="KeyFilterConfig"/>. Mutually exclusive with <see cref="Sample"/>.</summary>
    public KeyFilterConfig? KeyFilter { get; set; }

    /// <summary>
    /// Stop each output after N records have been written. Null (default) = no cap. Combine with
    /// <c>load.splitEvery</c> to emit exactly one batch file (e.g. both set to 100000).
    /// </summary>
    public long? MaxRecords { get; set; }
}

/// <summary>
/// A parent-scoped sample: take the first <see cref="PrimaryRows"/> rows of the primary source, then
/// restrict every other source to the rows belonging to those parents. Unlike a per-source row cap, the
/// sampled records are COMPLETE: a customer with 5 brand rows still gets all 5, at any nesting depth.
///
/// How it works: a pre-pass reads the primary's first N rows and collects the distinct values of
/// <see cref="KeyColumn"/>. Every other source (children at any depth, and lookup sources) is then
/// filtered on read to rows whose <see cref="KeyColumn"/> is in that set — a streaming semi-join. Sources
/// that don't have the column are read in full (logged), since they cannot be scoped this way.
///
/// Cost: each referenced source is still SCANNED end-to-end (the matching rows can be anywhere in the
/// file), so this is bounded by read throughput, not by the full feed's sort/RAM cost — the external sorts
/// and in-memory lookup indexes downstream only ever see the surviving rows.
/// </summary>
public sealed class SampleConfig
{
    /// <summary>How many primary-source rows to take (e.g. 100000 corporate customers).</summary>
    public int PrimaryRows { get; set; }

    /// <summary>
    /// The column that scopes a row to a parent — the key shared by the primary and its descendants
    /// (e.g. "customer_id"). Compared as a raw ordinal string, exactly as the join keys are.
    /// </summary>
    public string KeyColumn { get; set; } = "";
}

/// <summary>
/// A parent-scoped filter to a specific, named set of key values (e.g. one or a handful of customer ids)
/// — the config-driven equivalent of hand-editing the primary source down to just the row(s) you want to
/// debug or reproduce. Uses the exact same scoping mechanism as <see cref="SampleConfig"/> (every source,
/// at any depth, is restricted to rows whose <see cref="KeyColumn"/> matches), except the key set is given
/// explicitly instead of taken from the first N primary rows — so a record for a specific, known key can
/// be produced without needing that key to appear early in the primary source.
/// </summary>
public sealed class KeyFilterConfig
{
    /// <summary>Same meaning as <see cref="SampleConfig.KeyColumn"/> — the column that scopes a row to a
    /// parent (e.g. "customer_id"), on the primary source and every other source alike.</summary>
    public string KeyColumn { get; set; } = "";

    /// <summary>The exact key value(s) to keep (e.g. ["1001"]). Compared as raw ordinal strings,
    /// exactly as join keys are — leading zeros/formatting must match the source data exactly.</summary>
    public List<string> Values { get; set; } = new();
}

/// <summary>A data source: a delimited file today; JSON/XML reserved for later.</summary>
public sealed class SourceConfig
{
    /// <summary>File path, resolved relative to the config file's folder (absolute also allowed).</summary>
    public string File { get; set; } = "";

    /// <summary>"delimited" (default). "json"/"xml" are reserved and not yet supported.</summary>
    public string Format { get; set; } = "delimited";

    /// <summary>Delimiter for delimited sources. Shorthands: "csv" (,), "tsv" (\t), "pipe" (|), "semicolon" (;).</summary>
    public string Delimiter { get; set; } = ",";

    public string Quote { get; set; } = "\"";
    public bool HasHeader { get; set; } = true;

    /// <summary>Optional per-column input typing. Columns not listed default to <see cref="DataType.String"/>.</summary>
    public Dictionary<string, InputFieldConfig> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional decryption settings for a source file that arrives PGP-encrypted (a ".pgp"/".gz.pgp" file
    /// name). Only needed when the private key isn't already in the local gpg keyring — see
    /// <see cref="DecryptConfig"/> and <c>EncryptedSourceCache</c>.
    /// </summary>
    public DecryptConfig? Decrypt { get; set; }
}

/// <summary>How to get a PGP-encrypted source's private key when it isn't already in the local gpg keyring.</summary>
public sealed class DecryptConfig
{
    /// <summary>Path to a PGP private key file to import into the local gpg keyring before decrypting.
    /// Resolved relative to the config file's folder (absolute also allowed). Importing an already-known
    /// key is a harmless no-op, so this is safe to leave set even once the key is imported for good.</summary>
    public string? PgpKeyFile { get; set; }

    /// <summary>Passphrase for the private key, if it has one. Used only when actually decrypting (not
    /// when importing) — never logged, and never passed as a plain command-line argument.</summary>
    public string? PgpPassphrase { get; set; }
}

/// <summary>Input-side typing for a source column. Input fields are typed but never transformed.</summary>
public sealed class InputFieldConfig
{
    public DataType Type { get; set; } = DataType.String;

    /// <summary>For Date/DateTime: the parse format. ISO-8601 is assumed when omitted.</summary>
    public string? Format { get; set; }

    /// <summary>Optional culture (e.g. "en-US") for parsing. Invariant when omitted.</summary>
    public string? Culture { get; set; }
}

/// <summary>One output feed.</summary>
public sealed class OutputConfig
{
    public string Name { get; set; } = "";
    public OutputMode Mode { get; set; } = OutputMode.Object;

    /// <summary>The primary/root source alias.</summary>
    public string Source { get; set; } = "";

    /// <summary>Root-level output fields (mapped from the primary source row).</summary>
    public List<FieldConfig> Fields { get; set; } = new();

    /// <summary>Nested child relationships (each may itself nest children — arbitrary depth).</summary>
    public List<ChildConfig> Children { get; set; } = new();

    // --- keyedMap mode only ---------------------------------------------------------------------
    /// <summary>Column on the primary source to group rows by (keyedMap mode).</summary>
    public string? GroupBy { get; set; }

    /// <summary>Pipeline that produces the dynamic property name for each row (keyedMap mode).</summary>
    public KeyExprConfig? KeyExpr { get; set; }

    /// <summary>Optional dedup within a group (keyedMap mode). Default: no dedup.</summary>
    public DedupConfig? Dedup { get; set; }

    /// <summary>Optional filter: the primary record must satisfy every condition (AND) to be emitted.</summary>
    public List<FilterConditionConfig>? Filter { get; set; }

    /// <summary>When true, records dropped by <see cref="Filter"/> are appended to a &lt;base&gt;_&lt;ts&gt;_filtered.csv.</summary>
    public bool FilterExcludedFile { get; set; }

    /// <summary>
    /// Object mode only: keep only the first primary row per distinct value of this column — e.g. one
    /// event record per order_id even though the primary source (line items) has many rows per order.
    /// Applied after <see cref="Filter"/>. The first-seen row becomes the record's own field values;
    /// children still join against ALL rows sharing that key via their own relationship, not just the
    /// one kept as primary.
    /// </summary>
    public string? DistinctBy { get; set; }

    /// <summary>
    /// Object mode only: emit a record only when at least one of these named relations (each a child
    /// relationship's <c>as</c> on the primary node) ends up with content after its own filters — e.g.
    /// corp customers that have at least one app contact. Records that fail are counted as filtered (and
    /// written to the filtered file when <see cref="FilterExcludedFile"/> is set). Checked after
    /// <see cref="Filter"/> and before <see cref="DistinctBy"/>, so a failing row never claims a distinct slot.
    /// </summary>
    public List<string>? RequireRelations { get; set; }

    public LoadConfig Load { get; set; } = new();
}

/// <summary>One filter condition on a primary-source column.</summary>
public sealed class FilterConditionConfig
{
    public string Column { get; set; } = "";

    /// <summary>onOrAfter | after | onOrBefore | before (date) · eq | ne | gt | ge | lt | le · in · empty | notEmpty.</summary>
    public string Op { get; set; } = "";

    /// <summary>Comparison value. For date ops: an ISO date or a relative token like "now-2years"/"now-90days".</summary>
    public JsonElement? Value { get; set; }
}

/// <summary>A nested child relationship node. Recursive via <see cref="Children"/>.</summary>
public sealed class ChildConfig
{
    public string Source { get; set; } = "";

    /// <summary>The JSON property the nested value is written under.</summary>
    public string As { get; set; } = "";

    public Cardinality Cardinality { get; set; } = Cardinality.Many;

    /// <summary>The join keys linking this child to its parent node.</summary>
    public JoinKeysConfig On { get; set; } = new();

    /// <summary>When true, this relation's join ignores case on both sides — e.g. an email address stored
    /// with different casing in each source still matches. Default false (ordinal/case-sensitive, matching
    /// every other join in this engine). Only affects this one relation, not the sources involved.</summary>
    public bool CaseInsensitiveJoin { get; set; }

    public List<FieldConfig> Fields { get; set; } = new();
    public List<ChildConfig> Children { get; set; } = new();

    /// <summary>Keep only the first child per distinct value of this column. Mutually exclusive with <see cref="MergeBy"/>.</summary>
    public string? DistinctBy { get; set; }

    /// <summary>
    /// Instead of one array entry per row, group rows by this column's value and merge them into ONE
    /// entry per distinct value: scalar/lookup fields come from the first row in the group; a nested
    /// object-group field (e.g. contact_properties) has every row's fields merged together into it, each
    /// row's own keys prefixed per <see cref="MergePrefix"/> (computed per row) plus an underscore — so
    /// e.g. the same email shared across two brands merges into one contact with both brands' properties,
    /// distinguished by prefix. An array relationship nested in that object-group (e.g. payment_cards)
    /// is NOT prefixed: it's written once as the deduped union of every row's matches. A row whose prefix can't be computed (e.g. an unmapped brand code with no
    /// default in a "map" step) is dropped from the merge with a warning, not fatal. Mutually exclusive
    /// with <see cref="DistinctBy"/>.
    /// </summary>
    public string? MergeBy { get; set; }

    /// <summary>Required when <see cref="MergeBy"/> is set: a pipeline (typically a "map" step) computing
    /// each row's prefix for its merged object-group fields — same shape as <see cref="KeyBy"/>. Omitting
    /// it merges fields unprefixed (later rows overwrite earlier ones on key collision).</summary>
    public KeyByConfig? MergePrefix { get; set; }

    /// <summary>Cap the number of children.</summary>
    public int? Limit { get; set; }

    /// <summary>Set by inline fields — see <see cref="FieldConfig.ShareDistinct"/>.</summary>
    public bool ShareDistinct { get; set; }

    /// <summary>
    /// Optional conditions (AND) a child row must satisfy to be included — e.g. only the line items
    /// whose status matches this event type. Evaluated against the CHILD source's raw columns, same as
    /// an output-level <c>filter</c>.
    /// </summary>
    public List<FilterConditionConfig>? Filter { get; set; }

    /// <summary>
    /// When set, this relationship renders as an object keyed by a derived string (e.g. a brand code
    /// mapped to a site_id) instead of a plain array — <c>{ "&lt;key&gt;": [ ... ] }</c>. Every
    /// relationship sharing this node's <see cref="As"/> name must agree on whether it is keyed.
    /// </summary>
    public KeyByConfig? KeyBy { get; set; }

    /// <summary>
    /// Keyed relationships only: when true, each key's value is a single object (the first matching
    /// child) instead of an array of all matching children.
    /// </summary>
    public bool KeyedSingle { get; set; }

    /// <summary>
    /// Keyed-single relationships only: when multiple children derive the same key, keep the one with
    /// the SMALLEST numeric value of this column (e.g. "rank_overall") instead of just the first-seen
    /// child. A row whose value doesn't parse as a number sorts last (loses to any parseable row).
    /// </summary>
    public string? KeyedSingleBestOf { get; set; }

    /// <summary>
    /// When true, this relationship's fields are spliced directly into the parent object instead of
    /// being nested under <see cref="As"/> — e.g. an order header joined onto an item row, with its
    /// fields rendered as siblings of the item's own fields rather than under a wrapper key. Requires
    /// <c>cardinality: "one"</c> (there's no sensible flat rendering of a many-relation), and this
    /// relationship must be the only one sharing its <see cref="As"/> name.
    /// </summary>
    public bool Inline { get; set; }
}

/// <summary>
/// Derives the dynamic property name for a keyed relationship: a pipeline (typically a <c>map</c> step)
/// run against a column on the CHILD row (e.g. "brand" -> "example-us-b1-dev").
/// </summary>
public sealed class KeyByConfig
{
    /// <summary>Column on the child source the pipeline starts from.</summary>
    public string? From { get; set; }

    public List<StepConfig> Pipeline { get; set; } = new();
}

/// <summary>
/// Parent/child column names that link a relationship. Use <see cref="Parent"/>/<see cref="Child"/> for a
/// single-column join, or <see cref="Parents"/>/<see cref="Children"/> for a composite (multi-column) join
/// (e.g. match on customer_id AND brand). Composite joins are supported by the hashInMemory strategy.
/// </summary>
public sealed class JoinKeysConfig
{
    public string Parent { get; set; } = "";
    public string Child { get; set; } = "";
    public List<string>? Parents { get; set; }
    public List<string>? Children { get; set; }
}

/// <summary>
/// An output field. In the common case: a source column (<see cref="From"/>), a final <see cref="Type"/>,
/// and an optional transform <see cref="Pipeline"/>. Two special shapes:
///   • <c>type: "object"</c> — an object-group: emits a nested object built from <see cref="Fields"/>
///     (same-row fields) and <see cref="Children"/> (relationships nested inside the group).
///   • <see cref="Lookup"/> set — the value is pulled from a related source rather than this row.
/// </summary>
public sealed class FieldConfig
{
    public string Name { get; set; } = "";

    /// <summary>Source column the pipeline starts from. May be omitted when the pipeline is fully literal-driven.</summary>
    public string? From { get; set; }

    public DataType Type { get; set; } = DataType.String;

    public List<StepConfig> Pipeline { get; set; } = new();

    // --- object-group (type: "object") ----------------------------------------------------------
    /// <summary>Nested fields. For an object-group: the object's fields. For an inline relationship: each element's fields.</summary>
    public List<FieldConfig>? Fields { get; set; }

    /// <summary>Relationships nested inside this object-group (joined against this node's row).</summary>
    public List<ChildConfig>? Children { get; set; }

    // --- inline relationship field (an array of objects placed at this position) ----------------
    /// <summary>When set, this field is a relationship rendered inline as an array of objects (built from <see cref="Fields"/>).</summary>
    public string? Source { get; set; }

    /// <summary>Join keys for the inline relationship.</summary>
    public JoinKeysConfig? On { get; set; }

    /// <summary>Inline relationship: ignore case on both sides of the join — same as
    /// <see cref="ChildConfig.CaseInsensitiveJoin"/>.</summary>
    public bool CaseInsensitiveJoin { get; set; }

    /// <summary>Inline relationship: keep only the first element per distinct value of this column.</summary>
    public string? DistinctBy { get; set; }

    /// <summary>Inline relationship: cap the number of elements.</summary>
    public int? Limit { get; set; }

    /// <summary>Inline relationship: when several inline fields share one <c>name</c> they render as ONE array
    /// (concatenated in declaration order). Fields that set this share a single distinct set (by the value of
    /// each one's <see cref="DistinctBy"/> column) and a single <see cref="Limit"/> across the whole array, so
    /// the same value arriving from two sources is emitted once. Fields without it keep per-source behavior.</summary>
    public bool ShareDistinct { get; set; }

    /// <summary>Inline relationship: only elements passing this filter are included — same shape as
    /// <see cref="ChildConfig.Filter"/>.</summary>
    public List<FilterConditionConfig>? Filter { get; set; }

    // --- keysOf ---------------------------------------------------------------------------------
    /// <summary>
    /// When set, this array field's value is the union of every distinct key actually produced by the
    /// named KEYED relation(s) on this same node (e.g. <c>["site_contacts", "site_properties"]</c>)
    /// — not an independently-computed lookup. Use this instead of a lookup-based array whenever a field
    /// is meant to list "every site this record has data under": deriving it FROM those relations' own
    /// keys makes it structurally impossible for it to drift out of sync with what they actually contain,
    /// which a separately-computed lookup can silently do as new keyed relations/sources are added.
    /// </summary>
    public List<string>? KeysOf { get; set; }

    // --- lookup ---------------------------------------------------------------------------------
    /// <summary>When set, the field value is pulled from a related source (see <see cref="LookupConfig"/>).</summary>
    public LookupConfig? Lookup { get; set; }

    /// <summary>
    /// Extra lookups whose results are unioned into this field's array, alongside <see cref="Lookup"/>
    /// (required when this is set). Each is deduped by its own "distinct"; the overall union is deduped
    /// again. For an array that's "computed values from one source, PLUS a conditional constant derived
    /// from a different source" — e.g. per-brand site ids from one source, plus a literal id that only
    /// appears when a qualifying row exists in a different source.
    /// </summary>
    public List<LookupConfig>? Lookups { get; set; }

    // --- coalesce ---------------------------------------------------------------------------------
    /// <summary>
    /// When set, the field's value is the first non-empty candidate from this ordered list — each
    /// either a column on this row (<see cref="CoalesceCandidateConfig.From"/>) or a lookup on a
    /// related source (<see cref="CoalesceCandidateConfig.Lookup"/>). E.g. "email if present, else
    /// phone." Mutually exclusive with <see cref="From"/> and <see cref="Lookup"/>.
    /// </summary>
    public List<CoalesceCandidateConfig>? Coalesce { get; set; }

    /// <summary>
    /// Coalesce fields only: <c>"value"</c> (default) emits the winning candidate's value;
    /// <c>"label"</c> emits its <see cref="CoalesceCandidateConfig.Label"/> instead — pair a "value"
    /// field with a "label" field sharing the same candidates to get e.g. contact_value/contact_type.
    /// </summary>
    public string? CoalesceEmit { get; set; }

    /// <summary>
    /// Overrides the run's <c>onError</c> policy for THIS field only. Today the only value is
    /// <c>"warnAndNull"</c>: if the field's final type coercion fails (e.g. a date column that
    /// sometimes carries a month-day-only value with no year), omit just this field, keep the rest of
    /// the record, and log a <see cref="FieldWarning"/> instead of dropping/aborting the whole row.
    /// Null (default) means "use the run's onError policy," same as before this existed.
    /// </summary>
    public string? OnError { get; set; }

    /// <summary>
    /// When true and the field's computed value is null (e.g. a bare <c>{"op": "literal"}</c> with no
    /// "default"), the field is written as an explicit JSON <c>null</c> instead of being omitted — the
    /// one exception to the engine's normal "never emit null" rule. Default false.
    /// </summary>
    public bool EmitNull { get; set; }
}

/// <summary>One candidate in a <see cref="FieldConfig.Coalesce"/> chain: a column on this row, or a lookup.</summary>
public sealed class CoalesceCandidateConfig
{
    /// <summary>A column on this row. Mutually exclusive with <see cref="Lookup"/>.</summary>
    public string? From { get; set; }

    /// <summary>A lookup on a related source (rendered as a single scalar). Mutually exclusive with <see cref="From"/>.</summary>
    public LookupConfig? Lookup { get; set; }

    /// <summary>Emitted for this candidate when the field's <c>coalesceEmit</c> is <c>"label"</c>.</summary>
    public string? Label { get; set; }
}

/// <summary>
/// Pulls a value (or array of values) from a related source, joined to the current row. Used for
/// fields that don't live on the primary row — e.g. a single "first available" value, or a deduped
/// array of ids gathered from a child table.
/// </summary>
public sealed class LookupConfig
{
    /// <summary>The source alias to read from.</summary>
    public string Source { get; set; } = "";

    /// <summary>Join: <c>parent</c> is a column on the current row; <c>child</c> is a column on the lookup source.</summary>
    public JoinKeysConfig On { get; set; } = new();

    /// <summary>The lookup source column to project.</summary>
    public string Column { get; set; } = "";

    /// <summary>True → emit a single scalar (the first match). False → emit an array of matches.</summary>
    public bool Single { get; set; }

    /// <summary>For array lookups, drop duplicate values (preserving first-seen order).</summary>
    public bool Distinct { get; set; }

    /// <summary>For array lookups, cap the number of values (e.g. 1 = "first available").</summary>
    public int? Limit { get; set; }

    /// <summary>
    /// "values" (default) projects <see cref="Column"/> as usual. "exists" ignores <see cref="Column"/>
    /// and emits a bool: true when at least one row in <see cref="Source"/> joins to the current row AND
    /// (if given) satisfies every <see cref="Filter"/> condition.
    /// </summary>
    public string Mode { get; set; } = "values";

    /// <summary>Optional conditions (AND) a lookup-source row must satisfy to be indexed/counted.</summary>
    public List<FilterConditionConfig>? Filter { get; set; }

    /// <summary>
    /// When set, replaces <see cref="Column"/>: a pipeline evaluated once per lookup-source row (fields
    /// resolve against THAT row, e.g. "market"/"brand"), producing the indexed/projected string. Use this
    /// when the projected value isn't a single raw column (e.g. combining two columns via "concat").
    /// </summary>
    public List<StepConfig>? Expr { get; set; }

    /// <summary>Column on the lookup source's row <see cref="Expr"/> starts from (e.g. "brand", to feed
    /// a "map" step). Optional — omit when <see cref="Expr"/> only reads columns via "field" operands.</summary>
    public string? From { get; set; }
}

/// <summary>One transform step. The running value feeds the next step; extra operands are explicit.</summary>
public sealed class StepConfig
{
    public string Op { get; set; } = "";

    /// <summary>"throw" | "null" | { "default": &lt;value&gt; }. Defaults to throw.</summary>
    public JsonElement? OnError { get; set; }

    // Operand shapes used by various ops (only the relevant ones are read per op):
    public OperandConfig? Rhs { get; set; }              // arithmetic right-hand side
    public List<OperandConfig>? Values { get; set; }     // concat operands
    public string? Separator { get; set; }               // concat separator
    public string? Format { get; set; }                  // date/number format
    public string? Culture { get; set; }                 // date/number culture
    public Dictionary<string, JsonElement>? Cases { get; set; } // map: input (as string) -> literal
    public JsonElement? Default { get; set; }            // map: fallback when no case matches
}

/// <summary>A transform operand: a source field, a literal, or the running pipeline value.</summary>
public sealed class OperandConfig
{
    public string? Field { get; set; }
    public JsonElement? Literal { get; set; }

    /// <summary>When true, this operand is the current running pipeline value.</summary>
    public bool? Value { get; set; }
}

/// <summary>The keyedMap key expression (a transform pipeline yielding a string).</summary>
public sealed class KeyExprConfig
{
    public List<StepConfig> Pipeline { get; set; } = new();
}

/// <summary>Dedup within a keyedMap group: keep the row with the max of a field, per "within" value.</summary>
public sealed class DedupConfig
{
    /// <summary>Column whose value identifies duplicates within a group (e.g. email_address).</summary>
    public string Within { get; set; } = "";

    /// <summary>Column to maximize when choosing which duplicate to keep (e.g. email_status_date).</summary>
    public string KeepMaxOf { get; set; } = "";
}

/// <summary>Load (output) settings for one feed.</summary>
public sealed class LoadConfig
{
    /// <summary>"jsonl" today; the writer is pluggable for future formats.</summary>
    public string Format { get; set; } = "jsonl";

    /// <summary>
    /// Output folder for this feed. Supports <c>~</c> and paths relative to the config file. When omitted,
    /// the engine uses the fallback folder passed to <c>EtlEngine.Run</c> (the command prompts for one only
    /// if some output lacks a folder).
    /// </summary>
    public string? Folder { get; set; }

    /// <summary>Base file name; the timestamp (and split ordinal) are appended automatically.</summary>
    public string FileBaseName { get; set; } = "output";

    /// <summary>Roll to a new file every N records.</summary>
    public long SplitEvery { get; set; } = 1_000_000;

    /// <summary>
    /// Writes a random, uniform sample of N records from this feed as individual prettified JSON files,
    /// for quick eyeballing without opening a multi-GB jsonl file. On by default (see
    /// <see cref="SamplingConfig.Enabled"/>) — set <c>"sampling": { "enabled": false }</c> to turn it off.
    /// Object-mode outputs only.
    /// </summary>
    public SamplingConfig Sampling { get; set; } = new();

    /// <summary>
    /// Writes the N "richest" records — those with the most populated optional nested structures (e.g.
    /// payment_cards, household_member, multiple scoped sites) — as individual prettified JSON
    /// files, for eyeballing how the schema looks fully populated rather than a typical/mostly-empty
    /// record. Off by default. A separate, independent feature from <see cref="Sampling"/> (a uniform
    /// random sample) — enabling/configuring one does not affect the other.
    /// </summary>
    public RichSamplingConfig RichSampling { get; set; } = new();
}

/// <summary>Config for <see cref="LoadConfig.RichSampling"/>: a top-K sample by richness score, not a random one.</summary>
public sealed class RichSamplingConfig
{
    /// <summary>Off by default (unlike <see cref="SamplingConfig"/>, which is on by default).</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>How many of the richest records to keep.</summary>
    public int Count { get; set; } = 50;

    /// <summary>Subfolder (under the feed's output folder) the sample files are written to.</summary>
    public string Folder { get; set; } = "sampling/full";

    /// <summary>Same convention as <see cref="SamplingConfig.FileNameFields"/>.</summary>
    public List<string> FileNameFields { get; set; } = new() { "user_id", "first_name", "last_name" };
}

/// <summary>Config for <see cref="LoadConfig.Sampling"/>: a reservoir sample of pretty-printed records.</summary>
public sealed class SamplingConfig
{
    /// <summary>On by default.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How many records to sample.</summary>
    public int Count { get; set; } = 50;

    /// <summary>Subfolder (under the feed's output folder) the sample files are written to.</summary>
    public string Folder { get; set; } = "sampling";

    /// <summary>
    /// Top-level output field names joined with "_" to build each sample's file name (e.g. "user_id",
    /// "first_name", "last_name" -&gt; "1001_Ada_Lovelace.json"). Falls back to a random id when none
    /// of the configured fields are present on a given record.
    /// </summary>
    public List<string> FileNameFields { get; set; } = new() { "user_id", "first_name", "last_name" };
}
