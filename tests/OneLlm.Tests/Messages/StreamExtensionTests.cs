using System.Text;
using OneLlm.Messages;

namespace OneLlm.Tests.Messages;

/// <summary>The extension payload on stream events: provider fields with no neutral name reach the tool call and the message.</summary>
public class StreamExtensionTests
{
    private static ReadOnlyMemory<byte> Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void A_tool_call_start_with_an_extension_puts_it_in_the_calls_extension_data()
    {
        var acc = new MessageAccumulator();
        acc.Add(StreamEvent.ToolCallStart(0, "call_1", "shot", Utf8("""{"toolset_name":"computer","n":3,"caller":{"type":"direct"},"list":[1,"a",null]}""")));
        acc.Add(StreamEvent.ToolCallArgumentsDelta(0, Utf8("""{"x":1}""")));
        acc.Add(StreamEvent.ToolCallEnd(0));

        ToolCall call = Assert.Single(acc.ToMessage().ToolCalls);
        Assert.Equal("""{"x":1}""", call.Inputs!.ToJsonString());
        Assert.Equal("computer", call.ExtensionData!["toolset_name"]);
        Assert.Equal(3L, call.ExtensionData["n"]);
        Assert.Equal("direct", Assert.IsType<Dictionary<string, object?>>(call.ExtensionData["caller"])["type"]);
        Assert.Equal([1L, "a", null], Assert.IsType<List<object?>>(call.ExtensionData["list"]));
    }

    [Fact]
    public void A_tool_call_start_without_an_extension_has_no_extension_data()
    {
        var acc = new MessageAccumulator();
        acc.Add(StreamEvent.ToolCallStart(0, "call_1", "shot"));
        Assert.Null(Assert.Single(acc.ToMessage().ToolCalls).ExtensionData);
        Assert.True(StreamEvent.ToolCallStart(0, "c", "t").Extension.IsEmpty);
        Assert.True(StreamEvent.TextDelta(0, Utf8("{}")).Extension.IsEmpty);
    }

    [Fact]
    public void A_message_extension_is_kept_under_its_key_and_several_keys_coexist()
    {
        var acc = new MessageAccumulator();
        acc.Add(StreamEvent.TextDelta(0, Utf8("hi")));
        acc.Add(StreamEvent.MessageExtension("anthropic", Utf8("""{"container":{"id":"c_1"}}""")));
        acc.Add(StreamEvent.MessageExtension("other", Utf8("""{"a":1}""")));
        acc.Add(StreamEvent.Done(FinishReason.Stop));

        Message m = acc.ToMessage();
        var anthropic = Assert.IsType<Dictionary<string, object?>>(m.ExtensionData!["anthropic"]);
        Assert.Equal("c_1", Assert.IsType<Dictionary<string, object?>>(anthropic["container"])["id"]);
        Assert.Equal(1L, Assert.IsType<Dictionary<string, object?>>(m.ExtensionData["other"])["a"]);
    }

    [Fact]
    public void Without_a_message_extension_the_message_has_no_extension_data()
    {
        var acc = new MessageAccumulator();
        acc.Add(StreamEvent.TextDelta(0, Utf8("hi")));
        Assert.Null(acc.ToMessage().ExtensionData);
    }

    [Fact]
    public void The_extension_survives_a_json_round_trip_of_the_message()
    {
        var acc = new MessageAccumulator();
        acc.Add(StreamEvent.ToolCallStart(0, "call_1", "shot", Utf8("""{"toolset_name":"computer"}""")));
        acc.Add(StreamEvent.ToolCallArgumentsDelta(0, Utf8("{}")));
        acc.Add(StreamEvent.MessageExtension("anthropic", Utf8("""{"stop_sequence":"END"}""")));
        string json = System.Text.Json.JsonSerializer.Serialize(acc.ToMessage(), OneLlmJsonContext.Default.Message);
        Message back = System.Text.Json.JsonSerializer.Deserialize(json, OneLlmJsonContext.Default.Message)!;
        Assert.Equal(json, System.Text.Json.JsonSerializer.Serialize(back, OneLlmJsonContext.Default.Message));
        Assert.Equal("computer", back.ToolCalls[0].ExtensionData!["toolset_name"]);
    }
}
