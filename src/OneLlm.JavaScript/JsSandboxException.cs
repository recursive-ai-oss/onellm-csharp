using System.Text.Json.Nodes;

namespace OneLlm.JavaScript;

/// <summary>Why a sandboxed run failed.</summary>
public enum JsSandboxFailure
{
    /// <summary>The script threw, or a host function it called failed. the message says where.</summary>
    Script = 0,

    /// <summary>The source does not parse.</summary>
    Syntax = 1,

    /// <summary>The step budget ran out (<see cref="JsSandboxOptions.Fuel"/>).</summary>
    Fuel = 2,

    /// <summary>The allocation budget ran out (<see cref="JsSandboxOptions.MaxAllocatedBytes"/>).</summary>
    Memory = 3,

    /// <summary>The time limit passed.</summary>
    Time = 4,

    /// <summary>The script returned something that cannot become JSON, or too much of it.</summary>
    Result = 5,

    /// <summary>The host refused or ended the run to protect itself: too many scripts were running (<see cref="JsRunLimiter"/>), or the scripts together held too much memory.</summary>
    Busy = 6,
}

/// <summary>
/// A sandboxed run failed. The message is a short, positioned report written for a model to read: the error, the file name given to the run,
/// line and column, and the source line. It never contains host paths or stack traces from .NET.
/// </summary>
public sealed class JsSandboxException : OneLlmException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="kind">What kind of failure it was.</param>
    /// <param name="message">The report.</param>
    /// <param name="file">The name the source was run under, when known.</param>
    /// <param name="line">The 1-based line, or 0 when unknown.</param>
    /// <param name="column">The 1-based column, or 0 when unknown.</param>
    /// <param name="innerException">The engine exception behind it.</param>
    public JsSandboxException(JsSandboxFailure kind, string message, string? file = null, int line = 0, int column = 0, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        File = file;
        Line = line;
        Column = column;
    }

    /// <summary>What kind of failure it was.</summary>
    public JsSandboxFailure Kind { get; }

    /// <summary>The name the source was run under, when known.</summary>
    public string? File { get; }

    /// <summary>The 1-based line, or 0 when unknown.</summary>
    public int Line { get; }

    /// <summary>The 1-based column, or 0 when unknown.</summary>
    public int Column { get; }
}

/// <summary>What a sandboxed run produced.</summary>
public sealed class JsRunResult
{
    internal JsRunResult(JsonNode? value, bool hasValue, string output)
    {
        Value = value;
        HasValue = hasValue;
        Output = output;
    }

    /// <summary>The value the script returned, as JSON. Null when it returned nothing (<see cref="HasValue"/> false) or JSON <c>null</c>.</summary>
    public JsonNode? Value { get; }

    /// <summary>False when the script returned <c>undefined</c> (or nothing).</summary>
    public bool HasValue { get; }

    /// <summary>What the script wrote with <c>console.log</c>, <c>console.warn</c> and <c>console.error</c>, one line per call, capped by <see cref="JsSandboxOptions.MaxOutputChars"/>.</summary>
    public string Output { get; }
}
