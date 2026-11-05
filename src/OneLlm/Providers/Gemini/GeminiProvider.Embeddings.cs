using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Catalog;
using OneLlm.Messages;

namespace OneLlm.Providers.Gemini;

public sealed partial class GeminiProvider
{
    // batchEmbedContents takes a bounded number of requests; larger inputs are sent in several calls and joined in order.
    private const int EmbedBatchSize = 100;

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <inheritdoc />
    public async Task<EmbeddingResponse> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Inputs.Count == 0)
        {
            return new EmbeddingResponse { Embeddings = [], Model = request.Model };
        }

        ModelSpec? spec = SpecFor(request.Model);
        var vectors = new List<ReadOnlyMemory<float>>(request.Inputs.Count);
        long promptTokens = 0;
        for (int offset = 0; offset < request.Inputs.Count; offset += EmbedBatchSize)
        {
            string[] texts = [.. request.Inputs.Skip(offset).Take(EmbedBatchSize)];
            bool single = request.Inputs.Count == 1;
            byte[] body = EmbedBody(request, spec, texts, single);
            Uri uri = _endpoint.Model(request.Model, single ? "embedContent" : "batchEmbedContents");
            using JsonDocument doc = await SendForJsonAsync(HttpMethod.Post, uri, body, cancellationToken).ConfigureAwait(false);
            JsonElement root = doc.RootElement;
            if (single)
            {
                if (!root.TryGetProperty("embedding"u8, out JsonElement e))
                {
                    throw new MalformedResponseException(Name, "the embedContent answer has no embedding.");
                }

                vectors.Add(Values(e));
            }
            else
            {
                if (!root.TryGetProperty("embeddings"u8, out JsonElement list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() != texts.Length)
                {
                    throw new MalformedResponseException(Name, "the batchEmbedContents answer does not hold one embedding per input.");
                }

                foreach (JsonElement e in list.EnumerateArray())
                {
                    vectors.Add(Values(e));
                }
            }

            if (root.TryGetProperty("usageMetadata"u8, out JsonElement usage) && usage.TryGetProperty("promptTokenCount"u8, out JsonElement tokens) && tokens.TryGetInt64(out long n))
            {
                promptTokens += n;
            }
        }

        return new EmbeddingResponse
        {
            Embeddings = vectors,
            Model = request.Model,
            Usage = promptTokens > 0 ? new Usage { Model = request.Model, InputTokens = promptTokens } : null,
        };
    }

    private byte[] EmbedBody(EmbeddingRequest request, ModelSpec? spec, string[] texts, bool single)
    {
        // gemini-embedding-2 takes no task type: the task goes into the text as an instruction (the embeddings guide). Other models take taskType.
        bool instruction = spec?.Properties is { } props && props.TryGetValue("embedTask", out object? v) && v is "instruction";
        string resource = _endpoint.ModelResourceName(request.Model);
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartObject();
            if (!single)
            {
                w.WriteStartArray("requests");
            }

            foreach (string text in texts)
            {
                if (!single)
                {
                    w.WriteStartObject();
                }

                w.WriteString("model", resource);
                w.WritePropertyName("content");
                w.WriteStartObject();
                w.WriteStartArray("parts");
                w.WriteStartObject();
                w.WriteString("text", instruction ? WithInstruction(text, request.InputType) : text);
                w.WriteEndObject();
                w.WriteEndArray();
                w.WriteEndObject();
                if (!instruction && request.InputType != EmbeddingInputType.Unspecified)
                {
                    w.WriteString("taskType", request.InputType == EmbeddingInputType.Query ? "RETRIEVAL_QUERY" : "RETRIEVAL_DOCUMENT");
                }

                if (request.Dimensions is { } d)
                {
                    w.WriteNumber("outputDimensionality", d);
                }

                if (single)
                {
                    WriteExtras(w, request.ExtraParameters);
                }
                else
                {
                    w.WriteEndObject();
                }
            }

            if (!single)
            {
                w.WriteEndArray();
                WriteExtras(w, request.ExtraParameters);
            }

            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteExtras(Utf8JsonWriter w, JsonObject? extra)
    {
        if (extra is null)
        {
            return;
        }

        foreach ((string key, JsonNode? value) in extra)
        {
            w.WritePropertyName(key);
            if (value is null)
            {
                w.WriteNullValue();
            }
            else
            {
                value.WriteTo(w);
            }
        }
    }

    private static string WithInstruction(string text, EmbeddingInputType type) => type switch
    {
        EmbeddingInputType.Query => "task: search result | query: " + text,
        EmbeddingInputType.Document => "title: none | text: " + text,
        _ => text,
    };

    private ReadOnlyMemory<float> Values(JsonElement embedding)
    {
        if (!embedding.TryGetProperty("values"u8, out JsonElement values) || values.ValueKind != JsonValueKind.Array)
        {
            throw new MalformedResponseException(Name, "an embedding has no values.");
        }

        var result = new float[values.GetArrayLength()];
        int i = 0;
        foreach (JsonElement v in values.EnumerateArray())
        {
            result[i++] = v.GetSingle();
        }

        return result;
    }
}
