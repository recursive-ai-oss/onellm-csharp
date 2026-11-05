namespace OneLlm.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var app = new CliApp(Console.In, Console.Out, Console.Error) { Interactive = !Console.IsInputRedirected };
        return await app.RunAsync(args, cts.Token).ConfigureAwait(false);
    }
}
