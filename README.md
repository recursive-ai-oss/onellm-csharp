# OneLLM

**LLM inference and agent runtime for .NET.** Provider clients for Anthropic, OpenAI and Gemini, one message model, a layered model catalog with decimal line-item pricing, and a happy-path API, in a package whose core depends on the base class library, OneYaml and OneImage only. Trim and NativeAOT safe on the main paths.

NuGet: `RecursiveAI.OneLlm` (namespace `OneLlm`).

> OneLLM is unaffiliated with the `onellm` package on PyPI and with the OneLLM research model.

This repository is under construction; the API will change until the first release.

## Build

```
dotnet build OneLlm.slnx
dotnet test --solution OneLlm.slnx
```

Build output goes to the directory in `ONELLM_ARTIFACTS`, or to `artifacts/` in the repository.

## Licence

`MIT OR Apache-2.0`, Copyright (c) Recursive AI, LLC. See `LICENSE-MIT` and `LICENSE-APACHE`.
