using System.Text.Json.Nodes;
using OneLlm.Secrets;
using OneLlm.Tools;

namespace OneLlm.JavaScript;

/// <summary>Host-selected services for one JavaScript run. Scripts cannot enlarge this scope.</summary>
public sealed class JsHostContext
{
    /// <summary>Registry for ctx.callTool. Null uses the calling context's runtime registry, when present.</summary>
    public ToolRegistry? Tools { get; init; }

    /// <summary>Optional JSON identity exposed as ctx.identity. Callees receive the original ToolContext, including InstanceId and Services.</summary>
    public JsonNode? Identity { get; init; }

    /// <summary>Readable secrets. Null grants none. Available in either mode; no owner argument is exposed to script.</summary>
    public SecretScope? Secrets { get; init; }
}

/// <summary>Unrestricted grants used only inside an explicitly trusted JavaScript run.</summary>
internal sealed class TrustedCapabilities(string? directory) : ICapabilities
{
    public IReadOnlyList<string> FileRoots => [];
    public string? WorkingDirectory => directory;
    public bool ShellEnabled => true;
    public bool NetworkEnabled => true;
    public bool IsNetworkAllowed(Uri uri) => true;
    public bool IsAddressAllowed(Uri uri, System.Net.IPAddress address) => true;
    public string ResolveRead(string path) => Path.GetFullPath(path, directory ?? Environment.CurrentDirectory);
    public string ResolveWrite(string path) => ResolveRead(path);
}
