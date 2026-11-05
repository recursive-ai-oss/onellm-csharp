using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(OneLlm.Bench.VersionBench).Assembly).Run(args);
