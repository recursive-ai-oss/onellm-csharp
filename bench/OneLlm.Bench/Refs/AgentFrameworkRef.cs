using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace OneLlm.Bench.Refs;

/// <summary>
/// Microsoft Agent Framework's <c>ChatClientAgent</c> over a stub chat client that answers from memory: it asks for the no-op tool until
/// the conversation holds <c>Turns</c> tool results, then answers with text. The stub does no I/O, so what is measured is the agent
/// loop's own work, the function-invocation round trips included.
/// </summary>
public sealed class AgentFrameworkRef
{
    private readonly AIAgent _agent;

    public AgentFrameworkRef(int turns)
    {
        AIFunction noop = AIFunctionFactory.Create(() => "ok", "noop", "Does nothing.");
        _agent = new ChatClientAgent(new StubChatClient(turns), instructions: "You are an agent.", name: "bench", tools: [noop]);
    }

    public async Task<string> Run()
    {
        AgentResponse r = await _agent.RunAsync("go");
        return r.Text;
    }

    private sealed class StubChatClient(int turns) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            int results = 0;
            foreach (ChatMessage m in messages)
            {
                foreach (AIContent c in m.Contents)
                {
                    if (c is FunctionResultContent)
                    {
                        results++;
                    }
                }
            }

            ChatMessage reply = results < turns
                ? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-" + results, "noop", new Dictionary<string, object?>())])
                : new ChatMessage(ChatRole.Assistant, "done");
            return Task.FromResult(new ChatResponse(reply));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
