using System.Text.Json.Nodes;
using OneLlm;
using OneLlm.JavaScript;
using OneLlm.Messages;
using OneLlm.Tools;

// Loads a JavaScript tool file and runs it and run_javascript through the invoker, so a NativeAOT publish proves the sandbox,
// the YAML description reader and the JSON bridge work without reflection.
string dir = Path.Combine(Path.GetTempPath(), "onellm-js-aot-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
try
{
    string file = Path.Combine(dir, "double.js");
    File.WriteAllText(file, "export function invoke(i) { ctx.addCost({ counter: 'calls', quantity: 1, unitPrice: '0.001' }); return { twice: i.n * 2 }; }\n");
    File.WriteAllText(Path.Combine(dir, "double.tool.yaml"), "name: double\ndescription: Doubles a number.\ninputSchema:\n  type: object\n  properties:\n    n: { type: number }\n  required: [n]\n");

    ITool tool = JsTools.Load(file);
    var ctx = new ToolContext("smoke", "c1", TimeProvider.System) { Capabilities = Capabilities.Files(dir) };
    ToolResult r = await ToolInvoker.InvokeAsync(tool, new JsonObject { ["n"] = 21 }, ctx, CancellationToken.None);
    bool toolOk = !r.IsError && r.Parts[0] is JsonPart { Value: JsonObject o } && o["twice"]!.GetValue<int>() == 42 && r.CostLines.Count == 1 && r.CostLines[0].Amount == 0.001m;

    ITool run = JsTools.RunJavaScript();
    ToolResult s = await ToolInvoker.InvokeAsync(run, new JsonObject { ["code"] = "await ctx.writeFile('a.txt', 'hi'); console.log(await ctx.readFile('a.txt')); return [1, 2, 3].map(x => x * x);" }, ctx, CancellationToken.None);
    bool snippetOk = !s.IsError && ((TextPart)s.Parts[0]).Text == "hi\n=> [1,4,9]";

    ToolResult bad = await ToolInvoker.InvokeAsync(run, new JsonObject { ["code"] = "while (true) {}" }, new ToolContext("smoke", "c2", TimeProvider.System), CancellationToken.None);
    bool limitOk = bad.IsError;

    bool ok = toolOk && snippetOk && limitOk;
    Console.WriteLine(ok ? "ok" : $"FAILED tool={toolOk} snippet={snippetOk} limit={limitOk}");
    return ok ? 0 : 1;
}
finally
{
    Directory.Delete(dir, recursive: true);
}
