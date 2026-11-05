using System.Text.Json.Serialization;
using OneLlm;

// The happy path in one file. Usage: dotnet run --project samples/HappyPath -- <model> [embedding-model]
// The model is a catalog name or alias (for example gpt-5-mini); `provider:model` picks the provider explicitly.
// Keys come from the environment variable the catalog names for the provider, such as OPENAI_API_KEY.

string model = args.Length > 0 ? args[0] : "gpt-5-mini";

// Ask: one question, the text back.
string answer = await Llm.AskAsync(model, "In one sentence, what is a monad?");
Console.WriteLine(answer);

// Ask<T>: JSON that fits a type, sent through the provider's native structured output.
Invoice invoice = await Llm.AskAsync(model, "Extract the invoice: ACME Ltd billed 120.50 for consulting.", SampleJson.Default.Invoice);
Console.WriteLine($"{invoice.Customer}: {invoice.Total}");

// Stream: the text as it arrives.
await foreach (string piece in Llm.StreamTextAsync(model, "Write a haiku about build servers."))
{
    Console.Write(piece);
}

Console.WriteLine();

// The full result when you want usage and cost: every call is priced from the catalog, in line items.
AskResult detailed = await Llm.GenerateAsync(model, "Name three prime numbers.", []);
Console.WriteLine($"{detailed.Text}  [{detailed.Usage?.InputTokens} in, {detailed.Usage?.OutputTokens} out, ${detailed.Cost.Total}]");

// Chat: several turns that share one saved instance.
using ChatSession chat = Llm.Chat(model, "You answer in at most ten words.");
Console.WriteLine(await chat.AskAsync("What is the capital of France?"));
Console.WriteLine(await chat.AskAsync("And its population?"));

// Embed: needs an embedding model, so it runs only when one is named.
if (args.Length > 1)
{
    float[] vector = await Llm.EmbedAsync(args[1], "reset my password");
    Console.WriteLine($"embedding of {vector.Length} numbers");
}

// RunScriptAsync: a .flow file is a script of prompts with templates; each user prompt gets one model call.
ScriptResult script = await Llm.RunScriptAsync(
    Path.Combine(AppContext.BaseDirectory, "commit-message.flow"),
    new Dictionary<string, object?> { ["diff"] = "- retries = 1\n+ retries = 3" });
Console.WriteLine(script.Text);
Console.WriteLine($"script cost: ${script.Cost.Total} over {script.Turns.Count} calls");

internal sealed record Invoice(string Customer, decimal Total);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Invoice))]
internal sealed partial class SampleJson : JsonSerializerContext;
