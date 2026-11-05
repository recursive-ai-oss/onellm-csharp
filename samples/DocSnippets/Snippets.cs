using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using OneLlm.Agents;
using OneLlm.Deciders;
using OneLlm.Extensions.AI;
using OneLlm.Instances;
using OneLlm.Tools;
using OneLlm.Web;

namespace OneLlm.Samples.DocSnippets;

// The shapes the snippets use.
public sealed record Invoice(string Customer, decimal Total);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Invoice))]
public sealed partial class AppJson : JsonSerializerContext;

/// <summary>A tool class for the agent snippets.</summary>
public sealed partial class DocketTools
{
    /// <summary>Looks up a docket entry.</summary>
    /// <param name="caseNo">Case number, for example 24-1234.</param>
    [Tool]
    public static string GetDocket(string caseNo) => "Docket " + caseNo + ": open";
}

/// <summary>Every snippet of the README and the getting-started guide, in the order they appear there.</summary>
public static class Snippets
{
    public static async Task ReadmeAsync(CancellationToken ct)
    {
        // README: the happy path.
        string s = await Llm.AskAsync("claude-sonnet", "Summarize this", Asset.File("brief.pdf"), ct);
        Invoice inv = await Llm.AskAsync("gpt-6-luna", "Extract the invoice", Asset.File("scan.png"), AppJson.Default.Invoice, ct);
        await foreach (string piece in Llm.StreamTextAsync("gemini-flash", "Write a haiku", ct))
        {
            Console.Write(piece);
        }

        float[] v = await Llm.EmbedAsync("embedding-small", "reset my password", ct);
        using ChatSession chat = Llm.Chat("claude-sonnet");
        await chat.AskAsync("Name a prime.", ct);
        await chat.AskAsync("And the next one?", ct);

        AskResult r = await Llm.GenerateAsync("gpt-6-luna", "Name three prime numbers.", [], ct);
        Console.WriteLine($"{r.Text} [{r.Usage?.InputTokens} in, {r.Usage?.OutputTokens} out, {r.Cost.Total}]");
        Console.WriteLine(s + inv.Customer + v.Length);
    }

    public static async Task GettingStartedAsync(CancellationToken ct)
    {
        // Structured output with a source-generated context (trim and NativeAOT safe).
        Invoice invoice = await Llm.AskAsync("gpt-6-luna", "ACME Ltd billed 120.50 for consulting.", AppJson.Default.Invoice, ct);
        Console.WriteLine($"{invoice.Customer}: {invoice.Total}");

        // Streaming with the final result.
        StreamingAnswer stream = Llm.StreamAsync("claude-haiku", "Write a haiku about build servers.");
        await foreach (string piece in stream.TextAsync(ct))
        {
            Console.Write(piece);
        }

        AskResult done = await stream.Result;
        Console.WriteLine(done.Cost.Total);

        // A script: a .flow file rendered with inputs, one model call per user prompt.
        ScriptResult script = await Llm.RunScriptAsync("prompts/commit-message.flow", new Dictionary<string, object?> { ["diff"] = "…" }, ct);
        Console.WriteLine(script.Text);

        // An agent with a C# tool, granted the working directory only (the default).
        Llm.Default.Tools.Add<DocketTools>();
        AgentResult agent = await Llm.RunAgentAsync("agents/clerk.yaml", new Dictionary<string, object?> { ["caseNo"] = "24-1234" }, ct);
        Console.WriteLine($"{agent.StopReason}: {agent.Text} ({agent.Cost.Total})");

        // A live agent: follow it, approve a held call, wait for the end.
        AgentRun run = await Llm.StartAgentAsync("agents/clerk.yaml", null, ct);
        await run.ApproveAsync(cancellationToken: ct);
        AgentResult end = await run.AwaitAsync(ct);
        Console.WriteLine(end.StopReason);

        // A decider.
        DecisionResult verdict = await Llm.DecideAsync("deciders/triage-ticket", new Dictionary<string, object?> { ["title"] = "Refund please" }, ct);
        Console.WriteLine(verdict.Outputs);
    }

    public static void GrantsAndRuntime()
    {
        // Grants belong to the host: files, an allow-listed shell, a short list of hosts.
        var runtime = new Runtime
        {
            Capabilities = Capabilities.Files("work")
                         + Capabilities.ReadOnlyFiles("reference")
                         + Capabilities.ShellCommands("git status")
                         + Capabilities.Network("api.example.com"),
            Store = new FileInstanceStore("runs"),
            SpendCap = 5m,
            InstanceSpendCap = 1m,
        };
        runtime.Tools.Add<DocketTools>();
        runtime.Tools.Add(new FetchWebpageMarkdownTool());
        Llm.Default = runtime;
    }

    public static void Bridge()
    {
        // A OneLLM model as a Microsoft.Extensions.AI client.
        var rt = new Runtime();
        IChatClient client = rt.AsChatClient("claude-sonnet");
        Console.WriteLine(client.GetType().Name);
    }
}
