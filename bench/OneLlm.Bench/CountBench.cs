using BenchmarkDotNet.Attributes;
using Microsoft.ML.Tokenizers;
using OneLlm.Counting;
using OneLlm.Providers;
using System.Text;

namespace OneLlm.Bench;

/// <summary>
/// Local token counting over a history of about 1k, 50k or 200k tokens. The reference counts exactly with an o200k tokenizer. The core
/// package ships no local tokenizer yet (its local tier arrives in an adapter), so what core counts without a provider is the calibrated
/// estimate; <c>Info_*</c> reports that for scale and is not compared with the exact count.
/// </summary>
[MemoryDiagnoser]
public class CountBench
{
    private ChatRequest _request = null!;
    private string _text = "";
    private Tokenizer _tokenizer = null!;
    private readonly TokenCounter _counter = new();

    [Params(1_000, 50_000, 200_000)]
    public int Tokens { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        List<Turn> h = Mock.History(Tokens);
        _request = Ours.Request(h);
        var sb = new StringBuilder();
        foreach (Turn t in h)
        {
            sb.Append(t.Text);
        }

        _text = sb.ToString();
        _tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");
    }

    [GlobalCleanup]
    public void Cleanup() => Peak.Record();

    [Benchmark, BenchmarkCategory("count")]
    public int Ref_MlTokenizers_O200k() => _tokenizer.CountTokens(_text);

    [Benchmark, BenchmarkCategory("count")]
    public TokenCount Info_CoreEstimate() => _counter.Count(_request);
}

/// <summary>Loading the shipped model catalog (every provider's models and prices) from its embedded files.</summary>
[MemoryDiagnoser]
public class CatalogBench
{
    [GlobalCleanup]
    public void Cleanup() => Peak.Record();

    [Benchmark, BenchmarkCategory("catalog")]
    public OneLlm.Catalog.ModelCatalog Own_LoadShippedCatalog() => OneLlm.Catalog.CatalogLoader.Load(null, null, _ => null);
}
