using BenchmarkDotNet.Attributes;

namespace OneLlm.Bench;

/// <summary>The smallest benchmark: it proves the suite builds and runs.</summary>
[MemoryDiagnoser]
public class VersionBench
{
    [Benchmark]
    public string Version() => AssemblyMarker.Version;
}
