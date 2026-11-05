using BenchmarkDotNet.Attributes;
using OneLlm.Bench.Refs;
using OneLlm.Providers;

namespace OneLlm.Bench;

/// <summary>
/// A whole non-streamed turn against the mock server with a one-line answer: build the request from a history of about 1k, 50k or 200k
/// tokens, serialize it, send it over loopback, and parse the small response. Every client does the same work and the answer is
/// tiny, so what differs is chiefly the request side. Methods named <c>Ours_*</c> are OneLLM; <c>Ref_*</c> are the black-box references.
/// </summary>
[MemoryDiagnoser]
public class RequestBench
{
    private readonly Ours _ours = new();
    private readonly OpenAiRef _openai = new();
    private readonly AnthropicRef _anthropic = new();
    private readonly GeminiRef _gemini = new();
    private MeaiRef _meai = null!;
    private List<Turn> _history = null!;
    private ChatRequest _request = null!;

    [Params(1_000, 50_000, 200_000)]
    public int Tokens { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Mock.Configure(events: 1, bytes: 16);
        _meai = new MeaiRef(_openai.Chat);
        _history = Mock.History(Tokens);
        _request = Ours.Request(_history);
    }

    [GlobalCleanup]
    public void Cleanup() => Peak.Record();

    [Benchmark, BenchmarkCategory("request-anthropic")]
    public Task<ChatResponse> Ours_Anthropic() => _ours.Anthropic.SendAsync(_request);

    [Benchmark, BenchmarkCategory("request-anthropic")]
    public Task<string> Ref_AnthropicSdk() => _anthropic.Send(_history);

    [Benchmark, BenchmarkCategory("request-openai-responses")]
    public Task<ChatResponse> Ours_OpenAiResponses() => _ours.Responses.SendAsync(_request);

    [Benchmark, BenchmarkCategory("request-openai-responses")]
    public Task<string> Ref_OpenAiSdkResponses() => _openai.ResponsesSend(_history);

    [Benchmark, BenchmarkCategory("request-openai-chat")]
    public Task<ChatResponse> Ours_OpenAiChat() => _ours.ChatCompletions.SendAsync(_request);

    [Benchmark, BenchmarkCategory("request-openai-chat")]
    public Task<string> Ref_OpenAiSdkChat() => _openai.ChatSend(_history);

    [Benchmark, BenchmarkCategory("request-openai-chat")]
    public Task<string> Ref_MeaiOpenAiChat() => _meai.Send(_history);

    [Benchmark, BenchmarkCategory("request-gemini")]
    public Task<ChatResponse> Ours_Gemini() => _ours.Gemini.SendAsync(_request);

    [Benchmark, BenchmarkCategory("request-gemini")]
    public Task<string?> Ref_GeminiSdk() => _gemini.Send(_history);
}
