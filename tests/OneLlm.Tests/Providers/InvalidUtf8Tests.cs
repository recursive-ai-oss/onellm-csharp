using System.Text;
using System.Text.Json.Nodes;
using OneLlm.Providers;
using OneLlm.Providers.Anthropic;
using OneLlm.Providers.Gemini;
using OneLlm.Providers.OpenAI;

namespace OneLlm.Tests.Providers;

/// <summary>
/// A response that is not valid UTF-8 inside a JSON string must surface as the typed malformed-response error from every codec. The tree and
/// reader types only fail when such a string is first read, which used to escape as InvalidOperationException.
/// </summary>
public class InvalidUtf8Tests
{
    // The tilde stands for a lone 0xFF byte, which is never valid in UTF-8.
    private static byte[] Json(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'~')
            {
                bytes[i] = 0xFF;
            }
        }

        return bytes;
    }

    [Theory]
    [InlineData("""{"type":"content_block_start","index":0,"content_block":{"type":"text","text":"x~"}}""")]
    [InlineData("""{"type":"message_start","message":{"id":"msg_1","model":"x~","content":[],"usage":{}}}""")]
    [InlineData("""{"type":"error","error":{"type":"overloaded_error","message":"x~"}}""")]
    public void Anthropic_stream_events_with_invalid_utf8_are_malformed(string json)
    {
        var decoder = new MessageDecoder(s => s);
        Assert.Throws<MalformedResponseException>(() => decoder.DecodeEvent(Json(json)));
    }

    [Fact]
    public void Anthropic_whole_message_with_invalid_utf8_is_malformed()
    {
        JsonObject node = JsonNode.Parse(Json("""{"type":"message","id":"msg_1","role":"assistant","model":"m","content":[{"type":"text","text":"x~"}],"stop_reason":"end_turn","usage":{}}"""))!.AsObject();
        var decoder = new MessageDecoder(s => s);
        Assert.Throws<MalformedResponseException>(() => decoder.DecodeMessage(node));
    }

    [Theory]
    [InlineData("""{"type":"response.created","response":{"id":"x~","model":"m"}}""")]
    public void Responses_events_with_invalid_utf8_are_malformed(string json)
    {
        var decoder = new ResponsesDecoder("openai");
        Assert.Throws<MalformedResponseException>(() => decoder.ProcessEvent(Json(json)));
    }

    [Fact]
    public void Responses_body_with_invalid_utf8_is_malformed()
    {
        var decoder = new ResponsesDecoder("openai");
        Assert.Throws<MalformedResponseException>(() => decoder.ProcessResponse(Json("""{"id":"x~","model":"m","status":"completed","output":[]}""")));
    }

    [Fact]
    public void Chat_completions_chunk_with_invalid_utf8_is_malformed()
    {
        var decoder = new ChatCompletionsDecoder("compat");
        Assert.Throws<MalformedResponseException>(() => decoder.Process(Json("""{"id":"x~","model":"m","choices":[{"index":0,"delta":{"content":"hi"}}]}""")));
    }

    [Fact]
    public void Gemini_response_with_invalid_utf8_is_malformed()
    {
        var parser = new GeminiResponseParser("gemini", "m");
        Assert.Throws<MalformedResponseException>(() => parser.Parse(Json("""{"responseId":"x~","candidates":[{"content":{"parts":[{"text":"hi"}]}}]}"""), []));
    }
}
