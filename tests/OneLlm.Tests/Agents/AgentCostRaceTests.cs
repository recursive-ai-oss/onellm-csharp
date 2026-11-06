using OneLlm.Agents;
using OneLlm.Instances;
using OneLlm.Messages;
using static OneLlm.Tests.Agents.AgentEnv;

namespace OneLlm.Tests.Agents;

public class AgentCostRaceTests
{
    [Fact]
    public async Task Pricing_a_history_that_another_thread_is_extending_never_throws()
    {
        // A parent prices a live child while the child's loop appends turns; the sum must not fail on the overlap.
        Instance instance = Instance.FromDefinition(Def("a", []), "i1");
        using var stop = new CancellationTokenSource();
        Task writer = Task.Run(() =>
        {
            int n = 0;
            while (!stop.IsCancellationRequested)
            {
                instance.Messages.Add(Message.FromAssistant("turn", "m" + n++));
                if (instance.Messages.Count > 5000)
                {
                    instance.Messages.Clear();
                }
            }
        });

        try
        {
            for (int i = 0; i < 20000; i++)
            {
                _ = LiveAgent.OwnCostOf(instance);
            }
        }
        finally
        {
            stop.Cancel();
            await writer;
        }
    }
}
