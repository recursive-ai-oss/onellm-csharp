using BenchmarkDotNet.Running;
using OneLlm.Bench;

// `smoke` checks every client against a running mock server; `report <dir>` turns BenchmarkDotNet's JSON output into the results table;
// anything else goes to BenchmarkDotNet. See bench/README.md.
if (args.Length > 0 && args[0] == "smoke")
{
    return await Smoke.RunAsync();
}

if (args.Length > 0 && args[0] == "report")
{
    return Report.Run(args);
}

BenchmarkSwitcher.FromAssembly(typeof(VersionBench).Assembly).Run(args);
return 0;
