using System.Text.Json.Nodes;
using OneLlm.Catalog;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Providers.Gemini;

namespace OneLlm.Tests.Gemini;

internal static partial class GeminiCases
{
    // A 1x1 PNG, base64.
    private const string TinyPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==";

    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static IEnumerable<Case> Structured()
    {
        const string sref = "https://ai.google.dev/gemini-api/docs/structured-output";
        JsonObject schema = Obj("""{"type":"object","properties":{"name":{"type":"string"},"total":{"type":"number"},"paid":{"type":"boolean"}},"required":["name","total"],"additionalProperties":false}""");
        yield return new Case("structured", "json-schema", sref, async p =>
        {
            ChatResponse r = await p.SendAsync(new ChatRequest
            {
                Model = Flash,
                Messages = [User("Extract: Acme owes 12.5 and has not paid.")],
                ResponseFormat = ResponseFormat.FromSchema("invoice", schema),
            });
            JsonNode value = JsonNode.Parse(r.Message.GetAssistantText())!;
            Assert.Equal("Acme", value["name"]!.GetValue<string>());
            Assert.Equal(12.5, value["total"]!.GetValue<double>());
        }, new Reply(200, Answer("""{"text":"{\"name\":\"Acme\",\"total\":12.5,\"paid\":false}"}""")));

        yield return new Case("structured", "any-json", sref, async p =>
        {
            ChatResponse r = await p.SendAsync(new ChatRequest { Model = Flash, Messages = [User("Reply with a JSON object with one key.")], ResponseFormat = ResponseFormat.AnyJson });
            Assert.Equal("v", JsonNode.Parse(r.Message.GetAssistantText())!["k"]!.GetValue<string>());
        }, new Reply(200, Answer("""{"text":"{\"k\":\"v\"}"}""")));

        // A model whose catalog entry asks for the older OpenAPI-subset field.
        var layer = new CatalogLayer(CatalogLayer.Code, [new ProviderSpec { Name = "gemini", Models = [new ModelSpec { Name = Flash, Properties = new Dictionary<string, object?> { ["schemaForm"] = "responseSchema" } }] }]);
        yield return new Case("structured", "response-schema-subset", sref, async p =>
        {
            ChatResponse r = await p.SendAsync(new ChatRequest
            {
                Model = Flash,
                Messages = [User("Extract: Acme owes 12.5.")],
                ResponseFormat = ResponseFormat.FromSchema("invoice", schema),
            });
            Assert.Contains("Acme", r.Message.GetAssistantText(), StringComparison.Ordinal);
        }, new Reply(200, Answer("""{"text":"{\"name\":\"Acme\",\"total\":12.5}"}""")))
        {
            Catalog = ModelCatalog.Create([GeminiProvider.GetCatalogLayer(), layer]),
        };
    }

    private static IEnumerable<Case> Thinking()
    {
        yield return new Case("thinking", "level-with-summary", ThinkRef, async p =>
        {
            ChatResponse r = await p.SendAsync(new ChatRequest
            {
                Model = "gemini-3.8-flash",
                Messages = [User("Plan a two-step tidy-up.")],
                Reasoning = new ReasoningOptions { Effort = "high", Summary = "auto" },
            });
            var thought = Assert.IsType<ReasoningPart>(r.Message.Assistant[0]);
            Assert.Equal("Two steps: sort, then wipe.", thought.Display);
            Assert.Equal("thought", thought.Data!["on"]!.GetValue<string>());
            Assert.Equal(40, r.Usage!.Counters!["reasoningTokens"]);
            Assert.Equal(52, r.Usage.OutputTokens);

            // The next turn sends the signature back on the part it came with.
            ChatResponse second = await p.SendAsync(new ChatRequest
            {
                Model = "gemini-3.8-flash",
                Messages = [User("Plan a two-step tidy-up."), r.Message, User("Shorter, please.")],
                Reasoning = new ReasoningOptions { Effort = "high", Summary = "auto" },
            });
            Assert.Equal("Sort, wipe.", second.Message.GetAssistantText());
        },
        new Reply(200, Answer("""{"text":"Two steps: sort, then wipe.","thought":true},{"text":"1. Sort. 2. Wipe.","thoughtSignature":"c2lnLXBsYW4="}""", model: "gemini-3.8-flash", usage: """{"promptTokenCount":8,"candidatesTokenCount":12,"thoughtsTokenCount":40,"totalTokenCount":60}""")),
        new Reply(200, Answer("""{"text":"Sort, wipe."}""", model: "gemini-3.8-flash", id: "resp-0002")));

        yield return new Case("thinking", "budget", ThinkRef, async p =>
        {
            ChatResponse r = await p.SendAsync(new ChatRequest
            {
                Model = "gemini-2.5-flash",
                Messages = [User("What is 17 times 3?")],
                Reasoning = new ReasoningOptions { BudgetTokens = 1024 },
            });
            Assert.Equal("51", r.Message.GetAssistantText());
            Assert.Equal(300, r.Usage!.Counters!["reasoningTokens"]);
        }, new Reply(200, Answer("""{"text":"51"}""", model: "gemini-2.5-flash", usage: """{"promptTokenCount":9,"candidatesTokenCount":1,"thoughtsTokenCount":300,"totalTokenCount":310}""")));

        yield return new Case("thinking", "dynamic-and-off", ThinkRef, async p =>
        {
            await p.SendAsync(new ChatRequest { Model = "gemini-2.5-flash", Messages = [User("Hi.")], Reasoning = new ReasoningOptions { Effort = "disabled" } });
            ChatResponse r = await p.SendAsync(new ChatRequest { Model = "gemini-2.5-flash", Messages = [User("Hi again.")], Reasoning = new ReasoningOptions { Effort = "dynamic" } });
            Assert.Equal("Hello.", r.Message.GetAssistantText());
        },
        new Reply(200, Answer("""{"text":"Hello."}""", model: "gemini-2.5-flash")),
        new Reply(200, Answer("""{"text":"Hello."}""", model: "gemini-2.5-flash", id: "resp-0002")));
    }

    private static IEnumerable<Case> Media()
    {
        const string mref = "https://ai.google.dev/gemini-api/docs/image-understanding";
        yield return new Case("media", "inline-image", mref, async p =>
        {
            Message user = new() { User = [new TextPart("What is in the picture?"), new AssetPart(new AssetRef { Storage = "inline", Key = "pixel.png", ContentType = "image/png", Size = 70, Data = TinyPng })] };
            ChatResponse r = await p.SendAsync(new ChatRequest { Model = Flash, Messages = [user] });
            Assert.Equal("A single pixel.", r.Message.GetAssistantText());
            Assert.Equal(268, r.Usage!.InputTokens);
            Assert.Equal(258, r.Usage.Counters!["inputImageTokens"]);
        }, new Reply(200, Answer("""{"text":"A single pixel."}""", usage: """{"promptTokenCount":268,"candidatesTokenCount":4,"totalTokenCount":272,"promptTokensDetails":[{"modality":"TEXT","tokenCount":10},{"modality":"IMAGE","tokenCount":258}]}""")));

        yield return new Case("media", "inline-pdf-and-audio", "https://ai.google.dev/gemini-api/docs/document-processing", async p =>
        {
            Message user = new()
            {
                User =
                [
                    new TextPart("Compare the two."),
                    new AssetPart(new AssetRef { Storage = "inline", Key = "a.pdf", ContentType = "application/pdf", Size = 12, Data = "JVBERi0xLjQK" }),
                    new AssetPart(new AssetRef { Storage = "inline", Key = "a.wav", ContentType = "audio/wav", Size = 12, Data = "UklGRgAAAABXQVZF" }),
                ],
            };
            ChatResponse r = await p.SendAsync(new ChatRequest { Model = "gemini-2.5-flash", Messages = [user] });
            Assert.Equal("Different.", r.Message.GetAssistantText());
        }, new Reply(200, Answer("""{"text":"Different."}""", model: "gemini-2.5-flash")));

        yield return new Case("media", "file-reference", "https://ai.google.dev/api/files", async p =>
        {
            Message user = new() { User = [new TextPart("Summarize the file."), new FileRefPart("gemini", "abc-123") { ContentType = "application/pdf", Uri = "https://generativelanguage.googleapis.com/v1beta/files/abc-123" }] };
            ChatResponse r = await p.SendAsync(new ChatRequest { Model = Flash, Messages = [user] });
            Assert.Equal("A short file.", r.Message.GetAssistantText());
        }, new Reply(200, Answer("""{"text":"A short file."}""")));

        yield return new Case("media", "public-url-asset", mref, async p =>
        {
            Message user = new() { User = [new TextPart("Describe it."), new AssetPart(new AssetRef { Storage = "url", Key = "photo", ContentType = "image/jpeg", Size = 0 }) { PublicUrl = "https://example.com/photo.jpg" }] };
            ChatResponse r = await p.SendAsync(new ChatRequest { Model = Flash, Messages = [user] });
            Assert.Equal("A photo.", r.Message.GetAssistantText());
        }, new Reply(200, Answer("""{"text":"A photo."}""")));
    }

    private static IEnumerable<Case> Safety()
    {
        const string sref = "https://ai.google.dev/gemini-api/docs/safety-settings";
        yield return new Case("safety", "prompt-blocked", sref, async p =>
        {
            var e = await Assert.ThrowsAsync<ContentFilterException>(() => p.SendAsync(new ChatRequest { Model = Flash, Messages = [User("A request the filter refuses.")] }));
            Assert.Equal("SAFETY", e.Code);
        }, new Reply(200, """{"promptFeedback":{"blockReason":"SAFETY","safetyRatings":[{"category":"HARM_CATEGORY_DANGEROUS_CONTENT","probability":"HIGH","blocked":true}]},"usageMetadata":{"promptTokenCount":7,"totalTokenCount":7}}"""));

        yield return new Case("safety", "response-blocked", sref, async p =>
        {
            ChatResponse r = await p.SendAsync(new ChatRequest { Model = Flash, Messages = [User("Write something long.")] });
            Assert.Equal(FinishReason.ContentFilter, r.Finish);
            Assert.Equal("SAFETY", r.FinishReasonRaw);
            Assert.Equal("Once upon", r.Message.GetAssistantText());
        }, new Reply(200, Answer("""{"text":"Once upon"}""", finish: "SAFETY")));

        yield return new Case("safety", "max-tokens", GenRef, async p =>
        {
            ChatResponse r = await p.SendAsync(new ChatRequest { Model = Flash, Messages = [User("Count to a thousand.")], MaxOutputTokens = 5 });
            Assert.Equal(FinishReason.Length, r.Finish);
            Assert.Equal("MAX_TOKENS", r.FinishReasonRaw);
        }, new Reply(200, Answer("""{"text":"1 2 3 4 5"}""", finish: "MAX_TOKENS")));

        yield return new Case("safety", "no-content-recitation", GenRef, async p =>
        {
            ChatResponse r = await p.SendAsync(new ChatRequest { Model = Flash, Messages = [User("Recite a protected text.")] });
            Assert.Equal(FinishReason.ContentFilter, r.Finish);
            Assert.Equal("RECITATION", r.FinishReasonRaw);
            Assert.Equal("", r.Message.GetAssistantText());
        }, new Reply(200, """{"candidates":[{"finishReason":"RECITATION","index":0}],"usageMetadata":{"promptTokenCount":5,"totalTokenCount":5},"responseId":"resp-0009"}"""));
    }

    private static IEnumerable<Case> Resources()
    {
        yield return new Case("counting", "count-tokens", "https://ai.google.dev/api/tokens", async p =>
        {
            TokenCount n = await p.CountTokensAsync(new ChatRequest { Model = Flash, System = "Be brief.", Messages = [User("How many tokens is this?")], Tools = [Weather()] });
            Assert.Equal(31, n.InputTokens);
            Assert.Equal(TokenCountTier.Provider, n.Tier);
        }, new Reply(200, """{"totalTokens":31,"promptTokensDetails":[{"modality":"TEXT","tokenCount":31}]}"""));

        const string eref = "https://ai.google.dev/api/embeddings";
        yield return new Case("embeddings", "single", eref, async p =>
        {
            EmbeddingResponse r = await p.EmbedAsync(new EmbeddingRequest { Model = "gemini-embedding-001", Inputs = ["reset my password"], Dimensions = 3, InputType = EmbeddingInputType.Query });
            Assert.Equal([0.25f, -0.5f, 0.125f], r.Embeddings[0].ToArray());
            Assert.Equal(4, r.Usage!.InputTokens);
        }, new Reply(200, """{"embedding":{"values":[0.25,-0.5,0.125]},"usageMetadata":{"promptTokenCount":4}}"""));

        yield return new Case("embeddings", "batch", eref, async p =>
        {
            EmbeddingResponse r = await p.EmbedAsync(new EmbeddingRequest { Model = "gemini-embedding-001", Inputs = ["one", "two"], InputType = EmbeddingInputType.Document });
            Assert.Equal(2, r.Embeddings.Count);
            Assert.Equal(0.5f, r.Embeddings[1].Span[0]);
            Assert.Equal(6, r.Usage!.InputTokens);
        }, new Reply(200, """{"embeddings":[{"values":[0.1,0.2]},{"values":[0.5,0.6]}],"usageMetadata":{"promptTokenCount":6}}"""));

        yield return new Case("embeddings", "task-as-instruction", "https://ai.google.dev/gemini-api/docs/embeddings", async p =>
        {
            EmbeddingResponse r = await p.EmbedAsync(new EmbeddingRequest { Model = "gemini-embedding-2", Inputs = ["find the invoice"], InputType = EmbeddingInputType.Query, Dimensions = 768 });
            Assert.Single(r.Embeddings);
            Assert.Contains(r.Warnings, w => w.Code == "parameter_adjusted" && w.Parameter == "input type");
        }, new Reply(200, """{"embedding":{"values":[0.5,0.25]},"usageMetadata":{"promptTokenCount":9}}"""));

        const string cref = "https://ai.google.dev/api/caching";
        yield return new Case("caching", "explicit-create-use-delete", cref, async p =>
        {
            var source = new ChatRequest { Model = "gemini-2.5-flash", System = "You answer questions about the attached manual.", Messages = [User("MANUAL: " + new string('x', 40))] };
            GeminiCachedContent cache = await p.CreateCachedContentAsync(source, TimeSpan.FromHours(1), "manual");
            Assert.Equal("cachedContents/abc123", cache.Name);
            Assert.Equal(5000, cache.TotalTokens);
            Assert.NotNull(cache.ExpireTime);

            ChatResponse r = await p.SendAsync(new ChatRequest
            {
                Model = "gemini-2.5-flash",
                Messages = [User("What does chapter one say?")],
                Cache = new CachePolicy { CachedContent = cache.Name },
            });
            Assert.Equal("It says hello.", r.Message.GetAssistantText());
            Assert.Equal(5000, r.Usage!.CacheReadInputTokens);
            Assert.Equal(6, r.Usage.InputTokens);

            GeminiCachedContent updated = await p.UpdateCachedContentTtlAsync(cache.Name, TimeSpan.FromMinutes(10));
            Assert.Equal(cache.Name, updated.Name);
            GeminiCachedContent got = await p.GetCachedContentAsync("abc123");
            Assert.Equal("manual", got.DisplayName);
            await p.DeleteCachedContentAsync(cache.Name);
        },
        new Reply(200, """{"name":"cachedContents/abc123","model":"models/gemini-2.5-flash","displayName":"manual","createTime":"2026-11-05T10:00:00Z","updateTime":"2026-11-05T10:00:00Z","expireTime":"2026-11-05T11:00:00Z","usageMetadata":{"totalTokenCount":5000}}"""),
        new Reply(200, Answer("""{"text":"It says hello."}""", model: "gemini-2.5-flash", usage: """{"promptTokenCount":5006,"cachedContentTokenCount":5000,"candidatesTokenCount":4,"totalTokenCount":5010}""")),
        new Reply(200, """{"name":"cachedContents/abc123","model":"models/gemini-2.5-flash","displayName":"manual","createTime":"2026-11-05T10:00:00Z","expireTime":"2026-11-05T10:10:00Z","usageMetadata":{"totalTokenCount":5000}}"""),
        new Reply(200, """{"name":"cachedContents/abc123","model":"models/gemini-2.5-flash","displayName":"manual","createTime":"2026-11-05T10:00:00Z","expireTime":"2026-11-05T10:10:00Z","usageMetadata":{"totalTokenCount":5000}}"""),
        new Reply(200, "{}"));

        const string bref = "https://ai.google.dev/api/batch-mode";
        BatchRequest Two() => new()
        {
            Items =
            [
                new BatchItem { CustomId = "request-1", Chat = new ChatRequest { Model = "gemini-2.5-flash", Messages = [User("Say one.")] } },
                new BatchItem { CustomId = "request-2", Chat = new ChatRequest { Model = "gemini-2.5-flash", Messages = [User("Say two.")] } },
            ],
            ExtraParameters = Obj("""{"displayName":"two-items"}"""),
        };

        yield return new Case("batch", "submit-poll-inline-results", bref, async p =>
        {
            BatchJob job = await p.SubmitBatchAsync(Two());
            Assert.Equal("batches/b-1", job.Id);
            Assert.Equal(BatchStatus.Validating, job.Status);
            Assert.Equal(2, job.Total);

            BatchJob running = await p.GetBatchAsync(job.Id);
            Assert.Equal(BatchStatus.InProgress, running.Status);

            BatchJob done = await p.GetBatchAsync("b-1");
            Assert.Equal(BatchStatus.Completed, done.Status);
            Assert.Equal(1, done.Succeeded);
            Assert.Equal(1, done.Failed);

            var items = new List<BatchResultItem>();
            await foreach (BatchResultItem item in p.GetBatchResultsAsync(job.Id))
            {
                items.Add(item);
            }

            Assert.Equal(2, items.Count);
            Assert.Equal("request-1", items[0].CustomId);
            Assert.Equal("One.", items[0].Chat!.Message.GetAssistantText());
            Assert.Equal(5, items[0].Chat!.Usage!.InputTokens);
            Assert.Equal("request-2", items[1].CustomId);
            Assert.IsType<RateLimitException>(items[1].Error);
        },
        new Reply(200, """{"name":"batches/b-1","metadata":{"@type":"type.googleapis.com/google.ai.generativelanguage.v1main.GenerateContentBatch","model":"models/gemini-2.5-flash","displayName":"two-items","state":"BATCH_STATE_PENDING","createTime":"2026-11-05T10:00:00Z","batchStats":{"requestCount":"2","pendingRequestCount":"2"}}}"""),
        new Reply(200, """{"name":"batches/b-1","metadata":{"model":"models/gemini-2.5-flash","state":"BATCH_STATE_RUNNING","createTime":"2026-11-05T10:00:00Z","batchStats":{"requestCount":"2","pendingRequestCount":"1","successfulRequestCount":"1"}}}"""),
        new Reply(200, """{"name":"batches/b-1","done":true,"metadata":{"model":"models/gemini-2.5-flash","state":"BATCH_STATE_SUCCEEDED","createTime":"2026-11-05T10:00:00Z","batchStats":{"requestCount":"2","successfulRequestCount":"1","failedRequestCount":"1"}},"response":{"@type":"type.googleapis.com/google.ai.generativelanguage.v1main.GenerateContentBatchOutput","inlinedResponses":{"inlinedResponses":[{"metadata":{"key":"request-1"},"response":{"candidates":[{"content":{"role":"model","parts":[{"text":"One."}]},"finishReason":"STOP","index":0}],"usageMetadata":{"promptTokenCount":5,"candidatesTokenCount":2,"totalTokenCount":7},"modelVersion":"gemini-2.5-flash"}},{"metadata":{"key":"request-2"},"error":{"code":429,"message":"Quota exceeded for this request.","status":"RESOURCE_EXHAUSTED"}}]}}}"""),
        new Reply(200, """{"name":"batches/b-1","done":true,"metadata":{"model":"models/gemini-2.5-flash","state":"BATCH_STATE_SUCCEEDED","createTime":"2026-11-05T10:00:00Z","batchStats":{"requestCount":"2","successfulRequestCount":"1","failedRequestCount":"1"}},"response":{"@type":"type.googleapis.com/google.ai.generativelanguage.v1main.GenerateContentBatchOutput","inlinedResponses":{"inlinedResponses":[{"metadata":{"key":"request-1"},"response":{"candidates":[{"content":{"role":"model","parts":[{"text":"One."}]},"finishReason":"STOP","index":0}],"usageMetadata":{"promptTokenCount":5,"candidatesTokenCount":2,"totalTokenCount":7},"modelVersion":"gemini-2.5-flash"}},{"metadata":{"key":"request-2"},"error":{"code":429,"message":"Quota exceeded for this request.","status":"RESOURCE_EXHAUSTED"}}]}}}"""));

        yield return new Case("batch", "results-file", bref, async p =>
        {
            var items = new List<BatchResultItem>();
            await foreach (BatchResultItem item in p.GetBatchResultsAsync("batches/b-2"))
            {
                items.Add(item);
            }

            Assert.Equal(["request-1", "request-2"], items.Select(i => i.CustomId));
            Assert.Equal("Two.", items[1].Chat!.Message.GetAssistantText());
        },
        new Reply(200, """{"name":"batches/b-2","done":true,"metadata":{"state":"BATCH_STATE_SUCCEEDED","batchStats":{"requestCount":"2","successfulRequestCount":"2"}},"response":{"responsesFile":"files/batch-out-2"}}"""),
        new Reply(200, "{\"key\":\"request-1\",\"response\":{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"One.\"}]},\"finishReason\":\"STOP\",\"index\":0}]}}\n{\"key\":\"request-2\",\"response\":{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"Two.\"}]},\"finishReason\":\"STOP\",\"index\":0}]}}\n"));

        yield return new Case("batch", "cancel", bref, async p =>
        {
            BatchJob job = await p.CancelBatchAsync("b-3");
            Assert.Equal(BatchStatus.Cancelled, job.Status);
        },
        new Reply(200, "{}"),
        new Reply(200, """{"name":"batches/b-3","done":true,"metadata":{"state":"BATCH_STATE_CANCELLED","batchStats":{"requestCount":"2"}},"error":{"code":1,"message":"Cancelled."}}"""));

        yield return new Case("batch", "results-before-completion", bref, async p =>
        {
            await Assert.ThrowsAsync<BadRequestException>(async () =>
            {
                await foreach (BatchResultItem item in p.GetBatchResultsAsync("b-4"))
                {
                    _ = item;
                }
            });
        }, new Reply(200, """{"name":"batches/b-4","metadata":{"state":"BATCH_STATE_RUNNING","batchStats":{"requestCount":"2"}}}"""));
    }

    private static string ErrorBody(int code, string status, string message, string? details = null) =>
        "{\"error\":{\"code\":" + code + ",\"message\":\"" + message + "\",\"status\":\"" + status + "\"" + (details is null ? "" : ",\"details\":" + details) + "}}";

    private static IEnumerable<Case> Errors()
    {
        const string eref = "https://ai.google.dev/gemini-api/docs/troubleshooting";
        static ChatRequest Req() => new() { Model = Flash, Messages = [User("Hello.")] };

        yield return new Case("errors", "bad-request", eref, async p =>
        {
            var e = await Assert.ThrowsAsync<BadRequestException>(() => p.SendAsync(Req()));
            Assert.Equal(400, e.StatusCode);
            Assert.Equal("INVALID_ARGUMENT", e.Code);
        }, new Reply(400, ErrorBody(400, "INVALID_ARGUMENT", "Request contains an invalid argument.")));

        yield return new Case("errors", "context-overflow", eref, async p =>
        {
            var e = await Assert.ThrowsAsync<ContextOverflowException>(() => p.SendAsync(Req()));
            Assert.Equal(1100000, e.RequestedTokens);
            Assert.Equal(1048576, e.ContextWindow);
        }, new Reply(400, ErrorBody(400, "INVALID_ARGUMENT", "The input token count (1100000) exceeds the maximum number of tokens allowed (1048576).")));

        yield return new Case("errors", "unauthenticated-key", eref, async p =>
        {
            var e = await Assert.ThrowsAsync<AuthException>(() => p.SendAsync(Req()));
            Assert.Equal(400, e.StatusCode);
        }, new Reply(400, ErrorBody(400, "INVALID_ARGUMENT", "API key not valid. Please pass a valid API key.", """[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"API_KEY_INVALID","domain":"googleapis.com"}]""")));

        yield return new Case("errors", "permission-denied", eref, async p =>
        {
            var e = await Assert.ThrowsAsync<AuthException>(() => p.SendAsync(Req()));
            Assert.Equal(403, e.StatusCode);
            Assert.Equal("PERMISSION_DENIED", e.Code);
        }, new Reply(403, ErrorBody(403, "PERMISSION_DENIED", "The caller does not have permission.")));

        yield return new Case("errors", "unauthorized", eref, async p =>
        {
            var e = await Assert.ThrowsAsync<AuthException>(() => p.SendAsync(Req()));
            Assert.Equal(401, e.StatusCode);
        }, new Reply(401, ErrorBody(401, "UNAUTHENTICATED", "Request had invalid authentication credentials.")));

        yield return new Case("errors", "not-found", eref, async p =>
        {
            var e = await Assert.ThrowsAsync<BadRequestException>(() => p.SendAsync(Req()));
            Assert.Equal(404, e.StatusCode);
            Assert.Equal("NOT_FOUND", e.Code);
        }, new Reply(404, ErrorBody(404, "NOT_FOUND", "models/gemini-3.5-flash-lite is not found for API version v1beta.")));

        yield return new Case("errors", "rate-limit-then-success", eref, async p =>
        {
            ChatResponse r = await p.SendAsync(Req());
            Assert.Equal("Hi.", r.Message.GetAssistantText());
        },
        new Reply(429, ErrorBody(429, "RESOURCE_EXHAUSTED", "Too many requests, retry later.", """[{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"2s"}]"""), RetryAfter: "2"),
        new Reply(200, Answer("""{"text":"Hi."}""")));

        yield return new Case("errors", "daily-quota-not-retried", eref, async p =>
        {
            var e = await Assert.ThrowsAsync<RateLimitException>(() => p.SendAsync(Req()));
            Assert.True(e.IsQuotaExhausted);
            Assert.False(e.IsTransient);
        }, new Reply(429, ErrorBody(429, "RESOURCE_EXHAUSTED", "You exceeded your current quota, please check your plan and billing details.", """[{"@type":"type.googleapis.com/google.rpc.QuotaFailure","violations":[{"quotaMetric":"generativelanguage.googleapis.com/generate_content_free_tier_requests","quotaId":"GenerateRequestsPerDayPerProjectPerModel-FreeTier"}]}]""")));

        yield return new Case("errors", "server-error-then-success", eref, async p =>
        {
            ChatResponse r = await p.SendAsync(Req());
            Assert.Equal("Hi.", r.Message.GetAssistantText());
        },
        new Reply(500, ErrorBody(500, "INTERNAL", "An internal error has occurred.")),
        new Reply(200, Answer("""{"text":"Hi."}""")));

        yield return new Case("errors", "overloaded-after-retries", eref, async p =>
        {
            var e = await Assert.ThrowsAsync<OverloadedException>(() => p.SendAsync(Req()));
            Assert.Equal(503, e.StatusCode);
            Assert.Equal("UNAVAILABLE", e.Code);
        },
        new Reply(503, ErrorBody(503, "UNAVAILABLE", "The model is overloaded. Please try again later.")),
        new Reply(503, ErrorBody(503, "UNAVAILABLE", "The model is overloaded. Please try again later.")),
        new Reply(503, ErrorBody(503, "UNAVAILABLE", "The model is overloaded. Please try again later.")));

        yield return new Case("errors", "deadline-exceeded", eref, async p =>
        {
            var e = await Assert.ThrowsAsync<ProviderException>(() => p.SendAsync(Req()));
            Assert.Equal(504, e.StatusCode);
        },
        new Reply(504, ErrorBody(504, "DEADLINE_EXCEEDED", "The service is unable to finish processing within the deadline.")),
        new Reply(504, ErrorBody(504, "DEADLINE_EXCEEDED", "The service is unable to finish processing within the deadline.")),
        new Reply(504, ErrorBody(504, "DEADLINE_EXCEEDED", "The service is unable to finish processing within the deadline.")));
    }
}
