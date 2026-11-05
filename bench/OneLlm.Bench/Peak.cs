using System.Diagnostics;
using System.Globalization;

namespace OneLlm.Bench;

/// <summary>
/// Records the peak working set of the benchmark process that is finishing, keyed by the benchmark's name, into the file named by
/// <c>BENCH_PEAK_FILE</c> (when set). BenchmarkDotNet runs each benchmark in its own process and passes its name on the command line.
/// </summary>
public static class Peak
{
    /// <summary>Appends <c>name TAB megabytes</c> for this process. Does nothing when <c>BENCH_PEAK_FILE</c> is unset.</summary>
    public static void Record()
    {
        string? file = Environment.GetEnvironmentVariable("BENCH_PEAK_FILE");
        if (string.IsNullOrEmpty(file))
        {
            return;
        }

        string[] args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--benchmarkName");
        if (i < 0 || i + 1 >= args.Length)
        {
            return;
        }

        long mb = Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024);
        File.AppendAllText(file, args[i + 1] + "\t" + mb.ToString(CultureInfo.InvariantCulture) + "\n");
    }
}
