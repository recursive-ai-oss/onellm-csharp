using BenchmarkDotNet.Attributes;
using OneLlm.Bench.Refs;
using OneLlm.Providers;

namespace OneLlm.Bench;

/// <summary>
/// Reading a streamed answer of 2,000 small text events (8 characters each) from the mock server over loopback, after a 1k-token request.
/// Throughput is 2,000 events divided by the time; per-event overhead is the time divided by 2,000. Each client reads every event and sums
/// the text it received.
/// </summary>
[MemoryDiagnoser]
public class StreamBench
{
    private readonly Ours _ours = new();
    private readonly OpenAiRef _openai = new();
    private readonly AnthropicRef _anthropic = new();
    private readonly GeminiRef _gemini = new();
    private MeaiRef _meai = null!;
    private List<Turn> _history = null!;
    private ChatRequest _request = null!;

    [GlobalSetup]
    public void Setup()
    {
        Mock.Configure(events: 2000, bytes: 8, flushEvery: 8);
        _meai = new MeaiRef(_openai.Chat);
        _history = Mock.History(1_000);
        _request = Ours.Request(_history);
    }

    [GlobalCleanup]
    public void Cleanup() => Peak.Record();

    [Benchmark, BenchmarkCategory("stream-anthropic")]
    public Task<long> Ours_Anthropic() => Ours.Stream(_ours.Anthropic, _request);

    [Benchmark, BenchmarkCategory("stream-anthropic")]
    public Task<long> Ref_AnthropicSdk() => _anthropic.Stream(_history);

    [Benchmark, BenchmarkCategory("stream-openai-responses")]
    public Task<long> Ours_OpenAiResponses() => Ours.Stream(_ours.Responses, _request);

    [Benchmark, BenchmarkCategory("stream-openai-responses")]
    public Task<long> Ref_OpenAiSdkResponses() => _openai.ResponsesStream(_history);

    [Benchmark, BenchmarkCategory("stream-openai-chat")]
    public Task<long> Ours_OpenAiChat() => Ours.Stream(_ours.ChatCompletions, _request);

    [Benchmark, BenchmarkCategory("stream-openai-chat")]
    public Task<long> Ref_OpenAiSdkChat() => _openai.ChatStream(_history);

    [Benchmark, BenchmarkCategory("stream-openai-chat")]
    public Task<long> Ref_MeaiOpenAiChat() => _meai.Stream(_history);

    [Benchmark, BenchmarkCategory("stream-gemini")]
    public Task<long> Ours_Gemini() => Ours.Stream(_ours.Gemini, _request);

    [Benchmark, BenchmarkCategory("stream-gemini")]
    public Task<long> Ref_GeminiSdk() => _gemini.Stream(_history);
}

/// <summary>
/// Time to the first text event over loopback: send a 1k-token request for a short stream and stop at the first text delta. The server
/// flushes the first event on its own, so buffering on the server side does not hide a slow client.
/// </summary>
[MemoryDiagnoser]
public class FirstEventBench
{
    private readonly Ours _ours = new();
    private readonly OpenAiRef _openai = new();
    private readonly AnthropicRef _anthropic = new();
    private readonly GeminiRef _gemini = new();
    private MeaiRef _meai = null!;
    private List<Turn> _history = null!;
    private ChatRequest _request = null!;

    [GlobalSetup]
    public void Setup()
    {
        Mock.Configure(events: 50, bytes: 8, flushEvery: 8);
        _meai = new MeaiRef(_openai.Chat);
        _history = Mock.History(1_000);
        _request = Ours.Request(_history);
    }

    [GlobalCleanup]
    public void Cleanup() => Peak.Record();

    [Benchmark, BenchmarkCategory("first-anthropic")]
    public Task<long> Ours_Anthropic() => Ours.First(_ours.Anthropic, _request);

    [Benchmark, BenchmarkCategory("first-anthropic")]
    public Task<long> Ref_AnthropicSdk() => _anthropic.First(_history);

    [Benchmark, BenchmarkCategory("first-openai-responses")]
    public Task<long> Ours_OpenAiResponses() => Ours.First(_ours.Responses, _request);

    [Benchmark, BenchmarkCategory("first-openai-responses")]
    public Task<long> Ref_OpenAiSdkResponses() => _openai.ResponsesFirst(_history);

    [Benchmark, BenchmarkCategory("first-openai-chat")]
    public Task<long> Ours_OpenAiChat() => Ours.First(_ours.ChatCompletions, _request);

    [Benchmark, BenchmarkCategory("first-openai-chat")]
    public Task<long> Ref_OpenAiSdkChat() => _openai.ChatFirst(_history);

    [Benchmark, BenchmarkCategory("first-openai-chat")]
    public Task<long> Ref_MeaiOpenAiChat() => _meai.First(_history);

    [Benchmark, BenchmarkCategory("first-gemini")]
    public Task<long> Ours_Gemini() => Ours.First(_ours.Gemini, _request);

    [Benchmark, BenchmarkCategory("first-gemini")]
    public Task<long> Ref_GeminiSdk() => _gemini.First(_history);
}
