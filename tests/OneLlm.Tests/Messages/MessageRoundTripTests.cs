using System.Text.Json;
using System.Text.Json.Nodes;
using OneLlm.Messages;
using OneYaml;

namespace OneLlm.Tests.Messages;

public class MessageRoundTripTests
{
    private static string ToJson(Message m) => JsonSerializer.Serialize(m, OneLlmJsonContext.Default.Message);

    private static Message FromJson(string json) => JsonSerializer.Deserialize(json, OneLlmJsonContext.Default.Message)!;

    private static string ToYaml(Message m) => YamlSerializer.Serialize(m, OneLlmYamlContext.Default.Message);

    private static Message FromYaml(string yaml) => YamlSerializer.Deserialize(yaml, OneLlmYamlContext.Default.Message)!;

    [Fact]
    public void Json_round_trip_is_byte_stable()
    {
        string first = ToJson(SampleMessages.Everything());
        string second = ToJson(FromJson(first));
        Assert.Equal(first, second);
    }

    [Fact]
    public void Yaml_round_trip_is_byte_stable()
    {
        string first = ToYaml(SampleMessages.Everything());
        string second = ToYaml(FromYaml(first));
        Assert.Equal(first, second);
    }

    [Fact]
    public void Json_and_yaml_agree_with_each_other()
    {
        Message original = SampleMessages.Everything();
        // Whichever format a message was read from, it writes the same text in the other one.
        Assert.Equal(ToYaml(original), ToYaml(FromJson(ToJson(original))));
        Assert.Equal(ToJson(original), ToJson(FromYaml(ToYaml(original))));
    }

    [Fact]
    public void Every_part_kind_survives()
    {
        Message back = FromYaml(ToYaml(SampleMessages.Everything()));
        PartKind[] kinds = [.. back.Assistant.Select(p => p.Kind)];
        Assert.Equal(
            [PartKind.Reasoning, PartKind.Text, PartKind.HostedToolCall, PartKind.Refusal, PartKind.Json, PartKind.FileRef, PartKind.Asset, PartKind.Provider],
            kinds);

        var hosted = (HostedToolCallPart)back.Assistant[2];
        Assert.Equal("web_search", hosted.Tool);
        Assert.Equal(0.01m, hosted.Cost);
        Assert.Equal("weather", hosted.Inputs!.AsObject()["query"]!.GetValue<string>());
        var file = (FileRefPart)back.Assistant[5];
        Assert.Equal("files/abc", file.FileId);
        Assert.Equal("gemini", file.Provider);
        var asset = (AssetPart)back.Assistant[6];
        Assert.Equal(1024, asset.Asset!.Size);
        Assert.Equal(512, asset.ResizedImage!.Width);
        var text = (TextPart)back.Assistant[1];
        Assert.Equal("url_citation", text.Annotations![0].Type);
    }

    [Fact]
    public void Tool_calls_keep_their_place_between_parts()
    {
        Message back = FromJson(ToJson(SampleMessages.Everything()));
        Assert.Equal(2, back.ToolCalls.Count);
        Assert.All(back.ToolCalls, c => Assert.Equal(2, c.After));
        Assert.True(back.ToolCalls[1].IsError);
        Assert.Equal("/r/list", back.ToolCalls[1].Routine);
        Assert.Equal(0.002m, back.ToolCalls[0].Cost);
        Assert.True(back.ToolCalls[0].Injected);
    }

    [Fact]
    public void Unknown_fields_survive_at_every_level_in_json()
    {
        const string json = """
            {"id":"m","assistant":[{"text":"t","annotations":[{"type":"x","zAnn":1}],"zPart":{"deep":[1,2.5,null,true]}},
            {"asset":{"storage":"s","key":"k","contentType":"image/png","zAsset":"a"},"zAssetItem":3}],
            "toolCalls":[{"id":"c","tool":"t","inputs":{"a":1},"result":[{"text":"r","zRes":1}],"zCall":"c"}],
            "reasoning":[{"provider":"p","data":{"sig":"s"},"zReason":1}],
            "source":{"kind":"user","zSource":1},
            "usage":{"model":"m","inputTokens":1,"outputTokens":2,"cacheCreationInputTokens":0,"cacheReadInputTokens":0,"cost":0,"zUsage":{"k":"v"}},
            "zMessage":"kept"}
            """;
        string compact = JsonNode.Parse(json)!.ToJsonString();
        Message m = FromJson(json);
        JsonNode written = JsonNode.Parse(ToJson(m))!;

        // Every key of the input is in the output, at the same place. (Writing adds defaults such as the order index only when needed.)
        AssertContainsAll(JsonNode.Parse(compact)!, written, "$");
        Assert.Equal("kept", written["zMessage"]!.GetValue<string>());
        Assert.Equal(1, written["assistant"]![0]!["annotations"]![0]!["zAnn"]!.GetValue<int>());
        Assert.Equal(1, written["toolCalls"]![0]!["result"]![0]!["zRes"]!.GetValue<int>());
        Assert.Equal(1, written["reasoning"]![0]!["zReason"]!.GetValue<int>());
        Assert.Equal("v", written["usage"]!["zUsage"]!["k"]!.GetValue<string>());
        Assert.Equal(3, written["assistant"]![1]!["zAssetItem"]!.GetValue<int>());
        Assert.Equal("a", written["assistant"]![1]!["asset"]!["zAsset"]!.GetValue<string>());
    }

    private static void AssertContainsAll(JsonNode expected, JsonNode actual, string path)
    {
        switch (expected)
        {
            case JsonObject eo:
                var ao = Assert.IsType<JsonObject>(actual);
                foreach ((string key, JsonNode? value) in eo)
                {
                    Assert.True(ao.ContainsKey(key), $"{path}.{key} is missing");
                    AssertContainsAll(value!, ao[key]!, path + "." + key);
                }

                break;
            case JsonArray ea:
                var aa = Assert.IsType<JsonArray>(actual);
                Assert.True(aa.Count >= ea.Count, $"{path} lost items");
                for (int i = 0; i < ea.Count; i++)
                {
                    AssertContainsAll(ea[i]!, aa[i]!, $"{path}[{i}]");
                }

                break;
            default:
                if (expected is not null)
                {
                    Assert.Equal(expected.ToJsonString(), actual.ToJsonString());
                }

                break;
        }
    }

    [Fact]
    public void Unknown_fields_survive_in_yaml_and_text_is_unchanged()
    {
        // A message in the definition format with fields OneLLM does not model, at every level. Loading and saving must give the same text.
        const string yaml = """
            id: msg-1
            user:
              - text: hello
                extraUser: true
            assistant:
              - text: Sure.
                annotations:
                  - type: url_citation
                    url: https://example.com
                    zAnn: 1
                zPart:
                  nested:
                    - 1
                    - two
            toolCalls:
              - id: call-1
                tool: read_file
                inputs:
                  path: a.txt
                result:
                  - text: contents
                cost: 0.5
                zCall: kept
            reasoning:
              - provider: openai
                display: thinking
                data:
                  id: rs_1
                zReason: 1
            source:
              kind: agent
              instanceId: i-1
              zSource: x
            usage:
              model: m
              inputTokens: 10
              outputTokens: 20
              cacheCreationInputTokens: 0
              cacheReadInputTokens: 0
              cost: 0.25
              zUsage: 7
            zMessage:
              a: b
            """;
        Message m = FromYaml(yaml);
        Assert.Equal(yaml + "\n", ToYaml(m));
        Assert.Equal("kept", m.ToolCalls[0].ExtensionData!["zCall"]);
        Assert.Equal(7L, m.Usage!.ExtensionData!["zUsage"]);
    }

    [Fact]
    public void On_disk_lists_follow_the_definition_format()
    {
        // Reasoning, then text, then a tool call: the order a reader assumes without an index, so none is written.
        var m = new Message
        {
            Id = "m",
            Assistant = [new ReasoningPart("anthropic") { Display = "hm", Data = new JsonObject { ["signature"] = "s" } }, new TextPart("I will read it.")],
            ToolCalls = [new ToolCall("c1", "read_file") { Inputs = new JsonObject { ["path"] = "a" } }],
        };
        string yaml = ToYaml(m);
        Assert.Equal(
            """
            id: m
            assistant:
              - text: I will read it.
            toolCalls:
              - id: c1
                tool: read_file
                inputs:
                  path: a
            reasoning:
              - provider: anthropic
                display: hm
                data:
                  signature: s

            """,
            yaml);
        Message back = FromYaml(yaml);
        Assert.IsType<ReasoningPart>(back.Assistant[0]);
        Assert.IsType<TextPart>(back.Assistant[1]);
        Assert.Equal(int.MaxValue, back.ToolCalls[0].After);
    }

    [Fact]
    public void Order_index_is_written_only_when_the_order_is_not_the_default()
    {
        // Text, then reasoning, then a tool call between them: not the default order.
        var m = new Message
        {
            Assistant = [new TextPart("a"), new ReasoningPart("p"), new TextPart("b")],
            ToolCalls = [new ToolCall("c1", "t") { After = 1 }],
        };
        JsonNode json = JsonNode.Parse(ToJson(m))!;
        Assert.Equal(["a0", "t0", "r0", "a1"], json["partOrder"]!.AsArray().Select(n => n!.GetValue<string>()));
        Message back = FromJson(json.ToJsonString());
        Assert.IsType<TextPart>(back.Assistant[0]);
        Assert.IsType<ReasoningPart>(back.Assistant[1]);
        Assert.IsType<TextPart>(back.Assistant[2]);
        Assert.Equal(1, back.ToolCalls[0].After);

        // Non-definition parts live in extraParts and always bring the index.
        var withRefusal = new Message { Assistant = [new RefusalPart("no")] };
        JsonNode j2 = JsonNode.Parse(ToJson(withRefusal))!;
        Assert.Null(j2["assistant"]);
        Assert.Equal("no", j2["extraParts"]![0]!["refusal"]!.GetValue<string>());
        Assert.Equal("x0", j2["partOrder"]![0]!.GetValue<string>());
    }

    [Fact]
    public void A_hand_edited_or_partial_order_index_never_drops_content()
    {
        const string json = """
            {"id":"m","assistant":[{"text":"a"},{"text":"b"}],"reasoning":[{"provider":"p"}],
            "toolCalls":[{"id":"c","tool":"t"}],"partOrder":["a1","a1","zz","a9",""]}
            """;
        Message m = FromJson(json);
        // a1 first as the index says; the rest follow in the default order; the tool call stays.
        Assert.Equal(["b", "", "a"], m.Assistant.Select(p => p is TextPart t ? t.Text : ""));
        Assert.Single(m.ToolCalls);
    }

    [Fact]
    public void Empty_text_is_written_as_an_empty_item_and_read_back()
    {
        var m = new Message { Id = "e", User = [new TextPart("")] };
        string yaml = ToYaml(m);
        Assert.Contains("- {}", yaml, StringComparison.Ordinal);
        Assert.Equal("", ((TextPart)FromYaml(yaml).User[0]).Text);
    }

    [Fact]
    public void Timestamps_are_written_the_way_the_definition_format_writes_them()
    {
        var m = new Message { Id = "t", DateTime = new DateTimeOffset(2026, 3, 4, 5, 6, 7, 250, TimeSpan.Zero) };
        Assert.Contains("\"dateTime\":\"2026-03-04T05:06:07.25Z\"", ToJson(m), StringComparison.Ordinal);
        Message back = FromJson("""{"id":"t","dateTime":"2026-03-04T05:06:07.123456789+02:00"}""");
        Assert.Equal(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(2)).AddTicks(1234567), back.DateTime);
        Assert.Equal(TimeSpan.FromHours(2), back.DateTime!.Value.Offset);
    }

    [Fact]
    public void Legacy_inline_media_is_kept_as_it_was_read()
    {
        const string yaml = """
            id: m
            user:
              - image: aGVsbG8=
                mediaType: image/png
              - from: inputs.images
            """;
        Message m = FromYaml(yaml);
        Assert.IsType<AssetPart>(m.User[0]);
        Assert.Null(((AssetPart)m.User[0]).Asset);
        Assert.Equal(yaml + "\n", ToYaml(m));
    }
}
