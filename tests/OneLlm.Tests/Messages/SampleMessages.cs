using System.Text.Json.Nodes;
using OneLlm.Messages;

namespace OneLlm.Tests.Messages;

/// <summary>Synthetic messages that use every part kind and carry unknown fields at every level.</summary>
internal static class SampleMessages
{
    public static Message Everything() => new()
    {
        Id = "m1",
        DateTime = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero),
        Cache = true,
        Source = new MessageSource { Kind = "agent", InstanceId = "inst-1", ToolCallId = "call-9", ExtensionData = new() { ["hop"] = 2L } },
        Usage = new Usage
        {
            Model = "test-model",
            ServiceTier = "flex",
            InputTokens = 1200,
            OutputTokens = 340,
            CacheCreationInputTokens = 5,
            CacheReadInputTokens = 900,
            Cost = 0.0123m,
            Counters = new() { ["reasoningTokens"] = 120 },
            CostLines = [CostLine.Create("inputTokens", 1200, 1_000_000m, 3m, label: "input", source: "https://example.com/pricing", asOf: "2026-03-01")],
            ExtensionData = new() { ["latencyMs"] = 812L },
        },
        Assistant =
        [
            new ReasoningPart("anthropic")
            {
                Display = "thinking about it",
                Data = new JsonObject { ["signature"] = "c2lnbmF0dXJl", ["index"] = 0 },
                ExtensionData = new() { ["effort"] = "high" },
            },
            new TextPart("Here is the answer.")
            {
                Annotations = [new Annotation { Type = "url_citation", Title = "Example", Url = "https://example.com", Start = 0, End = 4, ExtensionData = new() { ["rank"] = 1L } }],
                ExtensionData = new() { ["note"] = "kept" },
            },
            new HostedToolCallPart("web_search")
            {
                Provider = "openai",
                Id = "ws_1",
                Status = "completed",
                Inputs = new JsonObject { ["query"] = "weather" },
                Result = new JsonArray(new JsonObject { ["url"] = "https://example.com" }),
                Cost = 0.01m,
                Data = new JsonObject { ["raw"] = true },
                ExtensionData = new() { ["extra"] = new List<object?> { 1L, "two" } },
            },
            new RefusalPart("I can't help with that."),
            new JsonPart(new JsonObject { ["name"] = "Ada", ["tags"] = new JsonArray("a", "b") }),
            new FileRefPart("gemini", "files/abc") { ContentType = "application/pdf", Name = "doc.pdf", Uri = "gs://b/doc.pdf" },
            new AssetPart(new AssetRef { Storage = "local", Key = "k/1.png", Hash = "sha256:ab", ContentType = "image/png", Size = 1024, Name = "1.png", ExtensionData = new() { ["etag"] = "x" } })
            {
                PublicUrl = "https://example.com/1.png",
                PublicUrlExpires = new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero),
                ResizedImage = new ResizedImage { MaxImageSize = 512, Width = 512, Height = 256, Asset = new AssetRef { Storage = "local", Key = "k/1s.png", ContentType = "image/png" } },
            },
            new ProviderPart("gemini", new JsonObject { ["executableCode"] = new JsonObject { ["code"] = "1+1" } }) { Type = "executableCode" },
        ],
        ToolCalls =
        [
            new ToolCall("call-1", "read_file")
            {
                Inputs = new JsonObject { ["path"] = "a.txt", ["limit"] = 10 },
                Result = [new TextPart("contents"), new JsonPart(new JsonObject { ["ok"] = true })],
                Cost = 0.002m,
                CostLines = [CostLine.Create("toolCalls", 1, 1m, 0.002m)],
                When = "{{ .inputs.x }}",
                Injected = true,
                InjectedBy = "inst-0",
                After = 2,
                ExtensionData = new() { ["durationMs"] = 12L },
            },
            new ToolCall("call-2", "list_files") { Routine = "/r/list", IsError = true, Result = [new TextPart("boom")], After = 2 },
        ],
        User = [new TextPart("hello"), new AssetPart(new AssetRef { Storage = "local", Key = "u.png", ContentType = "image/png" })],
        ExtensionData = new() { ["swarm"] = new Dictionary<string, object?> { ["id"] = "s1", ["depth"] = 3L, ["ratio"] = 0.5, ["flag"] = true, ["none"] = null } },
    };
}
