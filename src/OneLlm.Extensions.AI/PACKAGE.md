# RecursiveAI.OneLlm.Extensions.AI

**LLM inference and agent runtime for .NET**: the Microsoft.Extensions.AI bridge for OneLLM, in both directions. OneLLM is unaffiliated with the `onellm` package on PyPI and with the OneLLM research model.

- OneLLM models as `IChatClient`, `IEmbeddingGenerator<string, Embedding<float>>`, `ISpeechToTextClient`, `ITextToSpeechClient` and `IImageGenerator`, with usage and cost in `AdditionalProperties`.
- Any `IChatClient` (and embedding generator) as a OneLLM provider, given a catalog entry.
- `AIFunction` and OneLLM tools converted both ways.

```csharp
using OneLlm;
using OneLlm.Extensions.AI;
using Microsoft.Extensions.AI;

IChatClient client = new Runtime().AsChatClient("claude-sonnet");
ChatResponse response = await client.GetResponseAsync("Summarize this");
```

The speech, text-to-speech and image abstractions are experimental in Microsoft.Extensions.AI. What is carried and what is lost at the edges is in [the bridge notes](https://github.com/recursive-ai-oss/onellm-csharp/blob/v1.0.0/docs/extensions-ai.md).

Requires `RecursiveAI.OneLlm` and `Microsoft.Extensions.AI.Abstractions`. Source and issues: https://github.com/recursive-ai-oss/onellm-csharp

Licence: `MIT OR Apache-2.0`. Copyright (c) Recursive AI, LLC.
