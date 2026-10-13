using bleak.Data.Transformer.Config;

namespace bleak.Data.Transformer.Transforms;

/// <summary>
/// A single transformation. <paramref name="input"/> is the running pipeline value (the previous
/// step's output, or the field's source value for the first step). Extra operands are resolved against
/// the current row via <paramref name="row"/>. Implementations may throw; the wrapping <see cref="Step"/>
/// applies the configured <see cref="StepErrorMode"/>.
/// </summary>
public interface ITransform
{
    object? Apply(object? input, IRowContext row);
}

/// <summary>Read access to the current source row's typed column values, for operand resolution.</summary>
public interface IRowContext
{
    /// <summary>The typed value of a column in the current node's row (per the source's input types).</summary>
    object? GetField(string column);
}

/// <summary>A compiled operand: resolves to a value from the running value, a source field, or a literal.</summary>
public delegate object? Operand(object? running, IRowContext row);

/// <summary>Raised when a step fails and its error mode is <see cref="StepErrorMode.Throw"/>.</summary>
public sealed class EtlRowException : Exception
{
    public string Op { get; }

    public EtlRowException(string op, string message, Exception? inner = null)
        : base($"transform '{op}' failed: {message}", inner)
    {
        Op = op;
    }
}

/// <summary>An op plus its per-step error handling. Wraps an <see cref="ITransform"/>.</summary>
public sealed class Step
{
    private readonly ITransform _transform;
    private readonly StepErrorMode _errorMode;
    private readonly object? _defaultValue;

    public string Op { get; }

    public Step(string op, ITransform transform, StepErrorMode errorMode, object? defaultValue)
    {
        Op = op;
        _transform = transform;
        _errorMode = errorMode;
        _defaultValue = defaultValue;
    }

    public object? Run(object? input, IRowContext row)
    {
        try
        {
            return _transform.Apply(input, row);
        }
        catch (EtlRowException) when (_errorMode == StepErrorMode.Throw)
        {
            throw;
        }
        catch (Exception ex)
        {
            return _errorMode switch
            {
                StepErrorMode.Null => null,
                StepErrorMode.Default => _defaultValue,
                _ => throw new EtlRowException(Op, ex.Message, ex),
            };
        }
    }
}

/// <summary>Runs a compiled list of steps, threading each output into the next.</summary>
public static class Pipeline
{
    public static object? Execute(IReadOnlyList<Step> steps, object? start, IRowContext row)
    {
        var value = start;
        for (var i = 0; i < steps.Count; i++)
        {
            value = steps[i].Run(value, row);
        }
        return value;
    }
}
