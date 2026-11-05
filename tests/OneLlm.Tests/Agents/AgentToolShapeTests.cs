using System.Text.Json.Nodes;
using OneLlm.Agents;
using OneLlm.Tools;

namespace OneLlm.Tests.Agents;

/// <summary>
/// The agent tools keep the names, input properties, types and required lists that existing agent definitions rely on, so those
/// definitions run unchanged. The table below was taken from the shipped tool definitions the format came from.
/// </summary>
public class AgentToolShapeTests
{
    // tool => (properties as name:type[:detail], required)
    public static TheoryData<string, string, string> Shapes => new()
    {
        { "task_complete", "result:string,summary:string,title:string", "result,summary" },
        { "run_agent_async", "agentId:string,agentSwarmUri:string,inputs:object,prompt:string,swarm:string,workingDir:string", "agentSwarmUri,prompt" },
        { "await_agent", "ids:array<string>,mode:string[one|all]", "ids" },
        { "await_monitor", "ids:array<string>,mode:string[one|all]", "ids" },
        { "monitor_agent", "contextAt:string,elapsed:string,instanceId:string,onFinish:boolean,turns:integer", "instanceId" },
        { "list_agent_instances", "includeDescendants:boolean,limit:integer,name:string,state:string,swarm:string", "" },
        { "query_agent", "contains:string,fields:array<string>,fromMessageId:string,instanceId:string,lastN:integer,maxResultChars:integer,maxResults:integer,regex:string,role:string[user|assistant|tool],sourceKind:string[user|agent|runtime|api],toMessageId:string,toolName:string", "instanceId" },
        // The original takes no arguments and only points at run_agent_async; ours also accepts the two fields that make it work.
        { "message_agent", "agentId:string,prompt:string", "" },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Each_agent_tool_has_the_shape_existing_definitions_expect(string tool, string properties, string required)
    {
        Assert.True(new ToolRegistry().TryGet(tool, out ITool t), tool + " is registered");
        JsonObject schema = t.Definition.InputSchema;
        Assert.Equal("object", schema["type"]!.GetValue<string>());

        var actual = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach ((string name, JsonNode? value) in (JsonObject)schema["properties"]!)
        {
            string type = value!["type"]!.GetValue<string>();
            string detail = type == "array" ? $"<{value["items"]!["type"]!.GetValue<string>()}>" : "";
            string enumText = value["enum"] is JsonArray e ? "[" + string.Join('|', e.Select(static n => n!.GetValue<string>())) + "]" : "";
            actual[name] = name + ":" + type + detail + enumText;
        }

        Assert.Equal(properties, string.Join(',', actual.Values));
        string[] req = schema["required"] is JsonArray r ? [.. r.Select(static n => n!.GetValue<string>())] : [];
        Assert.Equal(required, string.Join(',', req));
    }

    [Fact]
    public void Task_complete_ends_the_loop_and_the_others_do_not()
    {
        var registry = new ToolRegistry();
        foreach (string name in registry.Names)
        {
            registry.TryGet(name, out ITool t);
            Assert.Equal(name == "task_complete", t.Definition.EndsLoop);
        }

        Assert.Equal(
            ["await_agent", "await_monitor", "list_agent_instances", "message_agent", "monitor_agent", "query_agent", "run_agent_async", "task_complete"],
            registry.Names);
    }
}
