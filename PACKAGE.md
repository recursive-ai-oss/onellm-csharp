# RecursiveAI.OneLlm

**LLM inference and agent runtime for .NET.** One message model and one set of calls over Anthropic, OpenAI and Gemini (first-party and on Azure, Bedrock and Vertex) and OpenAI-compatible servers; a layered model catalog that prices every call in `decimal` line items; and agents, scripts and deciders as files you can read, run, save and resume. The core depends on the base class library, OneYaml and OneImage only, and is trim and NativeAOT safe on its main paths. Requires .NET 10.

OneLLM is unaffiliated with the `onellm` package on PyPI and with the OneLLM research model.

```csharp
using OneLlm;

string answer = await Llm.AskAsync("claude-sonnet", "In one sentence, what is a monad?");
AskResult r   = await Llm.GenerateAsync("gpt-6-luna", "Name three prime numbers.", []);
Console.WriteLine($"{r.Text} costs {r.Cost.Total}");
```

Set `ANTHROPIC_API_KEY`, `OPENAI_API_KEY` or `GEMINI_API_KEY` for the provider you use.

- [Getting started](https://github.com/recursive-ai-oss/onellm-csharp/blob/v1.0.1/docs/getting-started.md)
- [The happy path and the runtime](https://github.com/recursive-ai-oss/onellm-csharp/blob/v1.0.1/docs/happy-path.md)
- [Agents](https://github.com/recursive-ai-oss/onellm-csharp/blob/v1.0.1/docs/agents.md), [capabilities](https://github.com/recursive-ai-oss/onellm-csharp/blob/v1.0.1/docs/capabilities.md), [deciders](https://github.com/recursive-ai-oss/onellm-csharp/blob/v1.0.1/docs/deciders.md)
- [Reads like](https://github.com/recursive-ai-oss/onellm-csharp/blob/v1.0.1/docs/reads-like.md): call-for-call tables from other .NET LLM libraries

Related packages: `RecursiveAI.OneLlm.Web`, `RecursiveAI.OneLlm.Extensions.AI`, `RecursiveAI.OneLlm.EntityFrameworkCore`, `RecursiveAI.OneLlm.OneLite`.

Source, documentation and issues: https://github.com/recursive-ai-oss/onellm-csharp

Licence: `MIT OR Apache-2.0`. Copyright (c) Recursive AI, LLC.
