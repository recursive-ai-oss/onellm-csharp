using System.Text.RegularExpressions;
using OneLlm.Agents;
using OneLlm.Catalog;
using OneLlm.Definitions;
using OneLlm.Instances;
using OneLlm.Messages;
using OneLlm.Providers;
using OneLlm.Testing;
using OneLlm.Tools;

namespace OneLlm.AotSmoke;

/// <summary>Tools written with the generator for the agent exercise: one needs approval.</summary>
internal sealed partial class Vault
{
    /// <summary>Looks up a balance.</summary>
    /// <param name="account">The account name.</param>
    [Tool]
    public static string Balance(string account) => account + ": 42";

    /// <summary>Moves money out of an account.</summary>
    /// <param name="account">The account name.</param>
    /// <param name="amount">The amount to move.</param>
    [Tool(RequiresApproval = true)]
    public static string Withdraw(string account, int amount) => "withdrew " + amount + " from " + account;
}

/// <summary>
/// An agent with a generated tool, an approval across a restart (a second runtime over the same file store) and a sub-agent, all on the
/// scripted provider: the loop, hooks, stores and agent tools under NativeAOT.
/// </summary>
internal static partial class AgentExercise
{
    [GeneratedRegex(@"Instance Id: (\S+)")]
    private static partial Regex InstanceId();

    private static Runtime NewRuntime(ScriptedProvider provider, string storeDir)
    {
        var runtime = new Runtime
        {
            Catalog = ModelCatalog.Create([CatalogLayer.FromYaml(CatalogLayer.Shipped, """
                name: scripted
                modelPatterns: ["demo-*"]
                models:
                  - name: demo-1
                    maxOutputTokens: 100
                    pricing: { inputPerMillion: 1.00, outputPerMillion: 2.00 }
                """)]),
            Providers = new ProviderRegistry(),
            Store = new FileInstanceStore(storeDir),
            IndependentSubAgentPolicy = true,
            RefreshAgentToolsOnRegistryChange = true,
            ResumeInterruptedAgents = true,
        };
        runtime.Providers.Set("scripted", provider);
        runtime.Tools.Add<Vault>();
        return runtime;
    }

    private static ScriptStep Calls(string id, string tool, string json) => new()
    {
        ToolCalls = [new ScriptedToolCall(id, tool, json)],
        Usage = new Usage { InputTokens = 1000, OutputTokens = 100 },
    };

    public static async Task<bool> RunAsync()
    {
        string dir = Path.Combine(Path.GetTempPath(), "onellm-aot-agents-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            AgentDefinition banker = AgentDefinition.Parse("name: banker\nmodel: demo-1\nsystemPrompt: BANKER\nreferences:\n  - tools: [balance, withdraw, task_complete, run_agent_async, await_agent]\n");
            AgentDefinition helper = AgentDefinition.Parse("name: helper\nmodel: demo-1\nsystemPrompt: HELPER\nreferences:\n  - tools: [task_complete]\n");

            // First process: the agent asks for a withdrawal and stops for approval.
            var first = new ScriptedProvider();
            first.Fallback = request => (request.System ?? "").Contains("HELPER", StringComparison.Ordinal)
                ? Calls("h1", "task_complete", """{"result":"helped","summary":"s"}""")
                : request.Messages.Count(static m => m.ToolCalls.Count > 0) switch
                {
                    0 => Calls("c1", "balance", """{"account":"main"}"""),
                    _ => Calls("c2", "withdraw", """{"account":"main","amount":5}"""),
                };
            Runtime one = NewRuntime(first, dir);
            if ((await one.ResumeInterruptedAgentsAsync()).Count != 0 || one.Tools.Version == 0 || one.Tools.Remove("absent"))
            {
                return false;
            }
            AgentRun run = await one.StartAgentAsync(banker, null, new AgentStartOptions { Prompt = "move money" });
            AgentResult held = await run.AwaitAsync();
            bool ok = held.StopReason == AgentStopReason.WaitingForApproval && held.PendingApprovals.Count == 1;
            string id = run.InstanceId;

            // Second process: reload from the file store, approve, delegate to a sub-agent, finish.
            var second = new ScriptedProvider();
            int turn = 0;
            second.Fallback = request =>
            {
                if ((request.System ?? "").Contains("HELPER", StringComparison.Ordinal))
                {
                    return Calls("h1", "task_complete", """{"result":"helped","summary":"s"}""");
                }

                int n = Interlocked.Increment(ref turn);
                if (n == 1)
                {
                    return Calls("c3", "run_agent_async", """{"agentSwarmUri":"helper","prompt":"assist"}""");
                }

                if (n == 2)
                {
                    string text = string.Concat(request.Messages.SelectMany(static m => m.ToolCalls).Where(static c => c.Tool == "run_agent_async").SelectMany(static c => c.Result.OfType<TextPart>()).Select(static p => p.Text));
                    return Calls("c4", "await_agent", "{\"ids\":[\"" + InstanceId().Match(text).Groups[1].Value + "\"]}");
                }

                return Calls("c5", "task_complete", """{"result":"all done","summary":"moved and helped"}""");
            };
            Runtime two = NewRuntime(second, dir);
            two.AgentDefinitions.Add("helper", helper);
            bool hooked = false;
            two.Hooks.AfterToolCall((_, _) =>
            {
                hooked = true;
                return new ValueTask<Hooks.HookDecision>(Hooks.HookDecision.Continue);
            });
            AgentRun again = (await two.GetAgentAsync(id))!;
            ok &= again.State == InstanceStates.WaitingForApproval;
            await again.ApproveAsync();
            AgentResult done = await again.AwaitAsync();

            ok &= done.StopReason == AgentStopReason.Completed
                && done.Text == "all done"
                && done.Instance.Messages.Any(static m => m.ToolCalls.Any(static c => c.Tool == "withdraw" && c.Result.OfType<TextPart>().Any(static p => p.Text == "withdrew 5 from main")))
                && done.SubtreeCost > done.Cost.Total
                && hooked;
            return ok;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
