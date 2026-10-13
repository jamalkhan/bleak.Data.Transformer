namespace bleak.Data.Transformer.Config;

/// <summary>The standard value types an input or output field can declare.</summary>
public enum DataType
{
    String,
    Integer,   // 64-bit
    Decimal,   // exact (money)
    Double,    // floating
    Bool,
    Date,      // date only
    DateTime,  // date + time
    Object,    // an object-group field (its own nested fields/relationships)
    Array,     // a JSON array value (e.g. from a split transform or an array lookup)
}

/// <summary>How relationships are joined for a run. Selected run-level (see design notes).</summary>
public enum JoinStrategyKind
{
    /// <summary>Stream the primary; index child sources in memory. Default.</summary>
    HashInMemory,

    /// <summary>Disk-backed external sort + merge join for direct children (bounded memory).</summary>
    ExternalMerge,
}

/// <summary>What the engine does when a step raises a row-level exception.</summary>
public enum RunErrorPolicy
{
    /// <summary>Drop the offending row, log it, and write it to a reject file. The run completes.</summary>
    Skip,

    /// <summary>Stop the whole run immediately.</summary>
    Abort,

    /// <summary>Set the failing field to null and keep the row.</summary>
    Nullify,
}

/// <summary>Nesting cardinality for a child relationship.</summary>
public enum Cardinality
{
    /// <summary>At most one matching child; nested as a JSON object (or omitted when none).</summary>
    One,

    /// <summary>Zero or more matching children; nested as a JSON array.</summary>
    Many,
}

/// <summary>The shape of an output feed.</summary>
public enum OutputMode
{
    /// <summary>One JSON object per primary row (with nested children).</summary>
    Object,

    /// <summary>One JSON object per group whose properties are dynamic keys (e.g. email::&lt;addr&gt;).</summary>
    KeyedMap,
}

/// <summary>How a single transform step reacts to its own failure.</summary>
public enum StepErrorMode
{
    /// <summary>Raise a row-level exception (handled by the run's <see cref="RunErrorPolicy"/>). Default.</summary>
    Throw,

    /// <summary>Quietly yield null and continue the pipeline.</summary>
    Null,

    /// <summary>Substitute a configured default value and continue.</summary>
    Default,
}

/// <summary>A field-level override of the run's <see cref="RunErrorPolicy"/> — see <c>FieldConfig.OnError</c>.</summary>
public enum FieldErrorMode
{
    /// <summary>No override: the run's <see cref="RunErrorPolicy"/> decides (skip the row, nullify, or abort). Default.</summary>
    Inherit,

    /// <summary>Omit just this field, keep the record, and log a warning instead of failing the row.</summary>
    WarnAndNull,
}
