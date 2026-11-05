using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OneLlm.Messages;
using OneLlm.Providers;

namespace OneLlm.Caching;

/// <summary>
/// An opt-in, in-memory cache of chat answers, keyed by a SHA-256 over everything that can change the answer: provider, model, system prompt,
/// every message part, tools, tool choice, response format, reasoning settings, output limit, sampling parameters, stop sequences, service tier
/// and extra parameters. It is off unless you create one and wrap a provider with <see cref="CachingChatProvider"/>. Thread-safe; the least recently
/// used entry is dropped at capacity.
/// </summary>
/// <remarks>
/// Settings that only steer provider-side caching or abuse monitoring (<see cref="ChatRequest.Cache"/>, <see cref="ChatRequest.UserId"/>) are not in the key.
/// Share one cache between providers only if they should see each other's answers; the provider name and model are part of the key.
/// </remarks>
public sealed class ResultCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, ChatResponse Value)>> _map = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, ChatResponse Value)> _order = new();
    private readonly int _capacity;

    /// <summary>Creates a cache.</summary>
    /// <param name="capacity">The most answers kept. Default 256.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is below 1.</exception>
    public ResultCache(int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    /// <summary>The number of answers held.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _map.Count;
            }
        }
    }

    /// <summary>Drops every answer.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _order.Clear();
        }
    }

    /// <summary>Looks an answer up and marks it as recently used.</summary>
    /// <param name="key">A key from <see cref="KeyFor"/>.</param>
    /// <param name="response">The answer, when found.</param>
    /// <returns>True when the cache holds the answer.</returns>
    public bool TryGet(string key, out ChatResponse? response)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            if (_map.TryGetValue(key, out LinkedListNode<(string Key, ChatResponse Value)>? node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                response = node.Value.Value;
                return true;
            }
        }

        response = null;
        return false;
    }

    /// <summary>Stores an answer, replacing any under the same key and dropping the least recently used answer past capacity.</summary>
    /// <param name="key">A key from <see cref="KeyFor"/>.</param>
    /// <param name="response">The answer.</param>
    public void Set(string key, ChatResponse response)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(response);
        lock (_gate)
        {
            if (_map.TryGetValue(key, out LinkedListNode<(string Key, ChatResponse Value)>? existing))
            {
                _order.Remove(existing);
                _map.Remove(key);
            }

            _map[key] = _order.AddFirst((key, response));
            while (_map.Count > _capacity)
            {
                LinkedListNode<(string Key, ChatResponse Value)> last = _order.Last!;
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }

    /// <summary>Computes the key of a request.</summary>
    /// <param name="provider">The provider's name; <see cref="IProvider.Name"/> and <see cref="IProvider.Deployment"/> joined by the caller if deployments should not share answers.</param>
    /// <param name="request">The request.</param>
    /// <returns>The key: 64 lowercase hexadecimal characters.</returns>
    public static string KeyFor(string provider, ChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(request);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var w = new FieldWriter(hash);
        w.Text(provider);
        w.Text(request.Model);
        w.Text(request.System);
        foreach (Message m in request.Messages)
        {
            w.Text("message");
            w.Bool(m.Cache);
            WriteParts(w, m.User);
            WriteParts(w, m.Assistant);
            foreach (ToolCall call in m.ToolCalls)
            {
                w.Text("call");
                w.Text(call.Id);
                w.Text(call.Tool);
                w.Json(call.Inputs);
                w.Bool(call.IsError);
                WriteParts(w, call.Result);
            }
        }

        w.Text("tools");
        foreach (ToolDefinition t in request.Tools)
        {
            w.Text(t.Name);
            w.Text(t.Description);
            w.Json(t.InputSchema);
            w.Bool(t.Strict);
        }

        foreach (HostedToolDefinition h in request.HostedTools)
        {
            w.Text("hosted");
            w.Text(h.Type);
            w.Json(h.Options);
        }

        w.Text(request.ToolChoice?.Mode.ToString());
        w.Text(request.ToolChoice?.ToolName);
        w.Text(request.ParallelToolCalls?.ToString());
        w.Text(request.ResponseFormat?.Kind.ToString());
        w.Text(request.ResponseFormat?.SchemaName);
        w.Json(request.ResponseFormat?.Schema);
        w.Text(request.ResponseFormat?.Strict.ToString());
        w.Text(request.Reasoning?.Effort);
        w.Text(request.Reasoning?.BudgetTokens?.ToString(CultureInfo.InvariantCulture));
        w.Text(request.Reasoning?.Summary);
        w.Text(request.MaxOutputTokens?.ToString(CultureInfo.InvariantCulture));
        w.Text(request.Temperature?.ToString("R", CultureInfo.InvariantCulture));
        w.Text(request.TopP?.ToString("R", CultureInfo.InvariantCulture));
        foreach (string stop in request.StopSequences)
        {
            w.Text(stop);
        }

        w.Text(request.ServiceTier);
        w.Json(request.ExtraParameters);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void WriteParts(FieldWriter w, IReadOnlyList<Part> parts)
    {
        w.Text("parts");
        foreach (Part part in parts)
        {
            w.Text(part.Kind.ToString());
            switch (part)
            {
                case TextPart t:
                    w.Text(t.Text);
                    break;
                case AssetPart a:
                    w.Text(a.Asset?.Storage);
                    w.Text(a.Asset?.Key);
                    w.Text(a.Asset?.Hash);
                    w.Text(a.Asset?.ContentType);
                    w.Text(a.Asset?.Data);
                    break;
                case ReasoningPart r:
                    w.Text(r.Provider);
                    w.Text(r.Display);
                    w.Json(r.Data);
                    break;
                case HostedToolCallPart h:
                    w.Text(h.Tool);
                    w.Json(h.Inputs);
                    w.Json(h.Result);
                    break;
                case RefusalPart r:
                    w.Text(r.Text);
                    break;
                case JsonPart j:
                    w.Json(j.Value);
                    break;
                case FileRefPart f:
                    w.Text(f.Provider);
                    w.Text(f.FileId);
                    break;
                case ProviderPart p:
                    w.Text(p.Provider);
                    w.Json(p.Raw);
                    break;
            }
        }
    }

    // Every field is length-prefixed, so ("ab", "c") and ("a", "bc") hash differently. The 8-byte prefix is a fixed small buffer.
    private readonly struct FieldWriter(IncrementalHash hash)
    {
        public void Text(string? value)
        {
            if (value is null)
            {
                Raw([], true);
                return;
            }

            Raw(Encoding.UTF8.GetBytes(value), false);
        }

        public void Bool(bool value) => Text(value ? "1" : "0");

        public void Json(JsonNode? node) => Text(node?.ToJsonString());

        private void Raw(ReadOnlySpan<byte> bytes, bool isNull)
        {
            // A null string (prefix -1) is distinct from an empty one.
            Span<byte> prefix = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(prefix, isNull ? -1 : bytes.Length);
            hash.AppendData(prefix);
            hash.AppendData(bytes);
        }
    }
}

/// <summary>
/// Wraps a chat provider with a <see cref="ResultCache"/>. A repeated <c>SendAsync</c> with an identical request returns the stored answer without
/// calling the provider; its <see cref="ChatResponse.Cost"/> is zero, while <see cref="ChatResponse.Usage"/> still describes the original call.
/// Streaming is never cached and passes straight through.
/// </summary>
public sealed class CachingChatProvider : IChatProvider
{
    private readonly IChatProvider _inner;
    private readonly ResultCache _cache;

    /// <summary>Creates the wrapper.</summary>
    /// <param name="inner">The provider to call on a miss.</param>
    /// <param name="cache">The cache; keep one instance to share answers between wrappers.</param>
    public CachingChatProvider(IChatProvider inner, ResultCache cache)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <inheritdoc />
    public string Name => _inner.Name;

    /// <inheritdoc />
    public string Deployment => _inner.Deployment;

    /// <inheritdoc />
    public async Task<ChatResponse> SendAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string key = ResultCache.KeyFor(_inner.Name + "/" + _inner.Deployment, request);
        if (_cache.TryGet(key, out ChatResponse? hit))
        {
            return hit! with { Cost = Cost.Zero };
        }

        ChatResponse response = await _inner.SendAsync(request, cancellationToken).ConfigureAwait(false);
        _cache.Set(key, response);
        return response;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
        _inner.StreamAsync(request, cancellationToken);
}
