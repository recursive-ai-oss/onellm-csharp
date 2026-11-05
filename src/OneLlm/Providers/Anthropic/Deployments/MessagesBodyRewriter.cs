using System.Buffers;
using System.Text.Json;

namespace OneLlm.Providers.Anthropic;

/// <summary>
/// Turns a Messages API request body into the body a cloud deployment takes. Bedrock and Vertex address the model in the URL, so the
/// <c>model</c> member is removed; they take the API version in the body (<c>anthropic_version</c>) and the beta features in a body array
/// (<c>anthropic_beta</c>) instead of headers; Bedrock selects streaming by operation, so its body carries no <c>stream</c> member, while
/// Vertex keeps it (Bedrock InvokeModel and Vertex rawPredict references; the cloud-platform pages of the Anthropic documentation).
/// </summary>
internal static class MessagesBodyRewriter
{
    /// <summary>Rewrites the body.</summary>
    /// <param name="body">The Messages API body the codec wrote.</param>
    /// <param name="anthropicVersion">The value of <c>anthropic_version</c>.</param>
    /// <param name="beta">Beta feature names for <c>anthropic_beta</c>; empty adds no member.</param>
    /// <param name="dropStream">True to remove <c>stream</c>.</param>
    /// <returns>The new body.</returns>
    public static ReadOnlyMemory<byte> Rewrite(ReadOnlyMemory<byte> body, string anthropicVersion, IReadOnlyList<string> beta, bool dropStream)
    {
        using JsonDocument doc = JsonDocument.Parse(body);
        var buffer = new ArrayBufferWriter<byte>(body.Length + 64);
        using (var w = new Utf8JsonWriter(buffer, AnthropicJson.WriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("anthropic_version", anthropicVersion);
            if (beta.Count > 0)
            {
                w.WriteStartArray("anthropic_beta");
                foreach (string b in beta)
                {
                    w.WriteStringValue(b);
                }

                w.WriteEndArray();
            }

            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
            {
                if (p.NameEquals("model") || p.NameEquals("anthropic_version") || (beta.Count > 0 && p.NameEquals("anthropic_beta")) || (dropStream && p.NameEquals("stream")))
                {
                    continue;
                }

                p.WriteTo(w);
            }

            w.WriteEndObject();
        }

        return buffer.WrittenMemory;
    }
}
