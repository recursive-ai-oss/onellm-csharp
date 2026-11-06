# RecursiveAI.OneLlm.Web

**LLM inference and agent runtime for .NET**: web tools for OneLLM. Adds `fetch_webpage_markdown`, which downloads a page and returns it as markdown for a model, using OneHtml. OneLLM is unaffiliated with the `onellm` package on PyPI and with the OneLLM research model.

```csharp
using OneLlm;
using OneLlm.Web;

var runtime = new Runtime { Capabilities = Capabilities.Network("docs.example.com") };
runtime.Tools.Add(new FetchWebpageMarkdownTool());
```

The tool needs the network grant: the host chooses which hosts a model may reach, and a definition cannot grant it. Every redirect hop is checked against the grant before it is requested. See [capabilities](https://github.com/recursive-ai-oss/onellm-csharp/blob/v1.0.1/docs/capabilities.md).

Requires `RecursiveAI.OneLlm`. Source and issues: https://github.com/recursive-ai-oss/onellm-csharp

Licence: `MIT OR Apache-2.0`. Copyright (c) Recursive AI, LLC.
