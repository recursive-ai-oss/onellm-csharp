namespace OneLlm.Messages;

/// <summary>A citation or marker over a range of a text part, in the provider-neutral shape.</summary>
public sealed class Annotation
{
    /// <summary>What the annotation is: <c>url_citation</c>, <c>file_citation</c>, <c>document_citation</c> or a provider's own name.</summary>
    public string Type { get; init; } = "";

    /// <summary>The cited page or document title.</summary>
    public string? Title { get; init; }

    /// <summary>The cited page URL.</summary>
    public string? Url { get; init; }

    /// <summary>The cited file or document id.</summary>
    public string? SourceId { get; init; }

    /// <summary>The cited passage, when the provider returns it.</summary>
    public string? CitedText { get; init; }

    /// <summary>Start of the annotated range in the text, as a UTF-16 index, when the provider says.</summary>
    public int? Start { get; init; }

    /// <summary>End of the annotated range (exclusive), as a UTF-16 index, when the provider says.</summary>
    public int? End { get; init; }

    /// <summary>Provider fields without a neutral name, kept as received.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }
}
