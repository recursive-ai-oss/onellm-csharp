using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OneLlm.Conformance;

/// <summary>
/// A recorded conversation with a provider: one or more request/response exchanges. Fixtures are JSON files; see the README next to this
/// project for the format. Request bodies are stored as JSON values so a reader sees them as written; response bodies likewise, and a
/// streamed response is stored as the chunks the stream delivered.
/// </summary>
public sealed class RecordedFixture
{
    /// <summary>The format version this harness reads and writes.</summary>
    public const int CurrentFormat = 1;

    /// <summary>The format version of the file.</summary>
    public int Format { get; set; } = CurrentFormat;

    /// <summary>What the fixture shows, in a sentence.</summary>
    public string? Description { get; set; }

    /// <summary>The exchanges, in the order they happened.</summary>
    public List<RecordedExchange> Exchanges { get; set; } = [];

    /// <summary>Reads a fixture from JSON text.</summary>
    /// <param name="json">The text.</param>
    /// <returns>The fixture.</returns>
    /// <exception cref="InvalidDataException">The text is not a fixture of a supported format.</exception>
    public static RecordedFixture Parse(string json)
    {
        JsonObject root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("A fixture is a JSON object.");
        int format = root["format"]?.GetValue<int>() ?? 0;
        if (format != CurrentFormat)
        {
            throw new InvalidDataException($"Unsupported fixture format {format}; this harness reads format {CurrentFormat}.");
        }

        var fixture = new RecordedFixture { Format = format, Description = root["description"]?.GetValue<string>() };
        foreach (JsonNode? e in root["exchanges"]?.AsArray() ?? [])
        {
            JsonObject exchange = e!.AsObject();
            fixture.Exchanges.Add(new RecordedExchange
            {
                Request = RecordedRequest.FromJson(exchange["request"]!.AsObject()),
                Response = RecordedResponse.FromJson(exchange["response"]!.AsObject()),
            });
        }

        return fixture;
    }

    /// <summary>Loads a fixture file.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The fixture.</returns>
    public static RecordedFixture Load(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    /// <summary>Writes the fixture as indented JSON.</summary>
    /// <returns>The text, ending in a line break.</returns>
    public string ToJson()
    {
        var root = new JsonObject { ["format"] = Format };
        if (Description is not null)
        {
            root["description"] = Description;
        }

        var exchanges = new JsonArray();
        foreach (RecordedExchange e in Exchanges)
        {
            exchanges.Add(new JsonObject { ["request"] = e.Request.ToJson(), ["response"] = e.Response.ToJson() });
        }

        root["exchanges"] = exchanges;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, NewLine = "\n", Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
    }
}

/// <summary>One request and the response it got.</summary>
public sealed class RecordedExchange
{
    /// <summary>The request.</summary>
    public RecordedRequest Request { get; set; } = new();

    /// <summary>The response.</summary>
    public RecordedResponse Response { get; set; } = new();
}

/// <summary>A message body as stored: JSON, text or bytes.</summary>
public sealed class RecordedBody
{
    /// <summary>The body as a JSON value, when it is JSON. Compared in order, with numbers by their text.</summary>
    public JsonNode? Json { get; set; }

    /// <summary>The body as UTF-8 text, when it is text but not JSON.</summary>
    public string? Text { get; set; }

    /// <summary>The body as bytes, for anything else (uploads).</summary>
    public byte[]? Bytes { get; set; }

    /// <summary>True when there is no body.</summary>
    public bool IsEmpty => Json is null && Text is null && Bytes is null;

    /// <summary>Stores raw bytes in the best form: JSON when they parse as JSON, text when they are UTF-8, otherwise bytes.</summary>
    /// <param name="bytes">The body.</param>
    /// <param name="looksJson">True when the content type says JSON, so a parse failure is stored as text instead of silently becoming bytes.</param>
    /// <returns>The stored body.</returns>
    public static RecordedBody FromBytes(byte[] bytes, bool looksJson)
    {
        if (bytes.Length == 0)
        {
            return new RecordedBody();
        }

        if (looksJson)
        {
            try
            {
                return new RecordedBody { Json = JsonNode.Parse(bytes, nodeOptions: null, new JsonDocumentOptions { MaxDepth = 256 }) };
            }
            catch (JsonException)
            {
                // Falls through to text: a body that claims to be JSON and is not is a fact worth recording.
            }
        }

        try
        {
            return new RecordedBody { Text = new UTF8Encoding(false, true).GetString(bytes) };
        }
        catch (DecoderFallbackException)
        {
            return new RecordedBody { Bytes = bytes };
        }
    }

    internal static RecordedBody FromJson(JsonObject owner)
    {
        var body = new RecordedBody();
        if (owner.TryGetPropertyValue("body", out JsonNode? json) && json is not null)
        {
            body.Json = json.DeepClone();
        }
        else if (owner["bodyText"] is JsonNode text)
        {
            body.Text = text.GetValue<string>();
        }
        else if (owner["bodyBase64"] is JsonNode b64)
        {
            body.Bytes = Convert.FromBase64String(b64.GetValue<string>());
        }

        return body;
    }

    internal void WriteTo(JsonObject owner)
    {
        if (Json is not null)
        {
            owner["body"] = Json.DeepClone();
        }
        else if (Text is not null)
        {
            owner["bodyText"] = Text;
        }
        else if (Bytes is not null)
        {
            owner["bodyBase64"] = Convert.ToBase64String(Bytes);
        }
    }

    /// <summary>The body as the bytes that went over the wire (JSON compact).</summary>
    /// <returns>The bytes.</returns>
    public byte[] ToWireBytes()
    {
        if (Json is not null)
        {
            return Encoding.UTF8.GetBytes(Json.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }

        if (Text is not null)
        {
            return Encoding.UTF8.GetBytes(Text);
        }

        return Bytes ?? [];
    }
}

/// <summary>A recorded request: what a replay must match.</summary>
public sealed class RecordedRequest
{
    /// <summary>The HTTP method.</summary>
    public string Method { get; set; } = "POST";

    /// <summary>The path and query, for example <c>/v1/messages</c> or <c>/v1beta/models/x:generateContent?alt=sse</c>.</summary>
    public string Path { get; set; } = "/";

    /// <summary>The headers that matter for the call (version and beta headers, content type). Authentication headers are never recorded. A replay checks each one named here and ignores the rest.</summary>
    public SortedDictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The body.</summary>
    public RecordedBody Body { get; set; } = new();

    internal static RecordedRequest FromJson(JsonObject o)
    {
        var r = new RecordedRequest
        {
            Method = o["method"]?.GetValue<string>() ?? "POST",
            Path = o["path"]?.GetValue<string>() ?? "/",
            Body = RecordedBody.FromJson(o),
        };
        foreach ((string name, JsonNode? value) in o["headers"]?.AsObject() ?? [])
        {
            r.Headers[name] = value!.GetValue<string>();
        }

        return r;
    }

    internal JsonObject ToJson()
    {
        var o = new JsonObject { ["method"] = Method, ["path"] = Path };
        if (Headers.Count > 0)
        {
            var h = new JsonObject();
            foreach ((string name, string value) in Headers)
            {
                h[name] = value;
            }

            o["headers"] = h;
        }

        Body.WriteTo(o);
        return o;
    }
}

/// <summary>A recorded response.</summary>
public sealed class RecordedResponse
{
    /// <summary>The HTTP status.</summary>
    public int Status { get; set; } = 200;

    /// <summary>The headers a client may read: content type, rate-limit and retry headers. Request ids and anything account-specific are left out.</summary>
    public SortedDictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The body, for a response that is not a stream.</summary>
    public RecordedBody Body { get; set; } = new();

    /// <summary>
    /// For a server-sent event stream: the chunks the stream delivered, each returned by one read. A chunk can end in the middle of an event
    /// to test a parser's buffering.
    /// </summary>
    public List<string>? Sse { get; set; }

    internal static RecordedResponse FromJson(JsonObject o)
    {
        var r = new RecordedResponse { Status = o["status"]?.GetValue<int>() ?? 200, Body = RecordedBody.FromJson(o) };
        foreach ((string name, JsonNode? value) in o["headers"]?.AsObject() ?? [])
        {
            r.Headers[name] = value!.GetValue<string>();
        }

        if (o["sse"] is JsonArray chunks)
        {
            r.Sse = [.. chunks.Select(c => c!.GetValue<string>())];
        }

        return r;
    }

    internal JsonObject ToJson()
    {
        var o = new JsonObject { ["status"] = Status };
        if (Headers.Count > 0)
        {
            var h = new JsonObject();
            foreach ((string name, string value) in Headers)
            {
                h[name] = value;
            }

            o["headers"] = h;
        }

        if (Sse is not null)
        {
            o["sse"] = new JsonArray([.. Sse.Select(c => (JsonNode)JsonValue.Create(c)!)]);
        }
        else
        {
            Body.WriteTo(o);
        }

        return o;
    }
}
