using System;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace OneLlm.Generators;

// Models handed from the transform to the output step hold only strings and numbers, never symbols, so the generator caches well:
// two models are equal when their text is.
internal sealed class LocationInfo
{
    public string Path { get; set; } = "";

    public TextSpan Span { get; set; }

    public LinePositionSpan Lines { get; set; }

    public static LocationInfo From(Location location) => new LocationInfo
    {
        Path = location.SourceTree?.FilePath ?? "",
        Span = location.SourceSpan,
        Lines = location.GetLineSpan().Span,
    };

    public Location ToLocation() => Location.Create(Path, Span, Lines);

    public override string ToString() => Path + ":" + Span.Start + ":" + Span.Length;
}

internal sealed class DiagInfo
{
    public DiagInfo(string id, string message, LocationInfo location)
    {
        Id = id;
        Message = message;
        Location = location;
    }

    public string Id { get; }

    public string Message { get; }

    public LocationInfo Location { get; }

    public Diagnostic ToDiagnostic() => Diagnostic.Create(
        new DiagnosticDescriptor(Id, Title(Id), "{0}", "OneLlm.Tools", DiagnosticSeverity.Error, isEnabledByDefault: true),
        Location.ToLocation(),
        Message);

    private static string Title(string id) => id switch
    {
        "OLT001" => "Tool methods need a partial class",
        "OLT002" => "Unsupported tool parameter",
        "OLT003" => "Unsupported tool return type",
        "OLT004" => "Tool methods cannot be generic",
        "OLT005" => "No JSON serializer context for a tool type",
        "OLT006" => "Recursive tool type",
        "OLT007" => "Duplicate tool name",
        "OLT008" => "Invalid tool name",
        "OLT009" => "Duplicate injected parameter",
        "OLT010" => "Unsupported containing type",
        _ => "Unsupported property naming policy",
    };

    public override string ToString() => Id + "|" + Message + "|" + Location;
}

internal sealed class ToolMethodModel : IEquatable<ToolMethodModel>
{
    private string _fingerprint = "";

    public LocationInfo Location { get; set; } = new LocationInfo();

    public string TypeKey { get; set; } = "";

    public string TypeName { get; set; } = "";

    public string Namespace { get; set; } = "";

    public string[] Headers { get; set; } = Array.Empty<string>();

    public bool IsStaticType { get; set; }

    public bool HasParameterlessConstructor { get; set; }

    public bool IsStaticMethod { get; set; }

    public string ToolName { get; set; } = "";

    public string ClassName { get; set; } = "";

    public string Code { get; set; } = "";

    public DiagInfo? Failure { get; set; }

    public void Seal()
    {
        var sb = new StringBuilder();
        sb.Append(TypeKey).Append('|').Append(Namespace).Append('|').Append(string.Join(",", Headers)).Append('|').Append(IsStaticType)
            .Append(HasParameterlessConstructor).Append(IsStaticMethod).Append('|').Append(ToolName).Append('|').Append(ClassName).Append('|').Append(Location)
            .Append('|').Append(Failure).Append('|').Append(Code);
        _fingerprint = sb.ToString();
    }

    public bool Equals(ToolMethodModel? other) => other is not null && string.Equals(_fingerprint, other._fingerprint, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as ToolMethodModel);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_fingerprint);
}
