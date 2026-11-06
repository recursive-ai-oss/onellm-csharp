# RecursiveAI.OneLlm.OneLite

**LLM inference and agent runtime for .NET**: an instance store and a record sink for OneLLM on a OneLite database file. Trim and NativeAOT compatible. OneLLM is unaffiliated with the `onellm` package on PyPI and with the OneLLM research model.

```csharp
using RecursiveAI.OneLite;
using OneLlm.OneLite;

using Database db = await OneLiteStorage.OpenAsync("onellm.db");
var store = await OneLiteInstanceStore.CreateAsync(db);
var sink  = await OneLiteRecordSink.CreateAsync(db);
```

One file holds many instances and their audit trail, and several processes can share it. See [stores](https://github.com/recursive-ai-oss/onellm-csharp/blob/v1.0.0/docs/stores.md).

Requires `RecursiveAI.OneLlm` and `RecursiveAI.OneLite`. Source and issues: https://github.com/recursive-ai-oss/onellm-csharp

Licence: `MIT OR Apache-2.0`. Copyright (c) Recursive AI, LLC.
