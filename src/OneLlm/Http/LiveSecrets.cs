using System.Collections;

namespace OneLlm.Http;

/// <summary>
/// A set of secrets that grows while a client is in use. Short-lived tokens (an OAuth access token, an Entra token) are minted after the
/// HTTP client was built, so a snapshot of the secrets taken at construction would never contain them; handing the client one of these
/// lets it scrub each token from error text as soon as the token exists.
/// </summary>
/// <remarks>Thread-safe. Only the most recent <see cref="MaxKept"/> secrets are kept: a token that old is no longer valid and is not sent again.</remarks>
public sealed class LiveSecrets : IReadOnlyList<Secret>
{
    /// <summary>How many secrets are kept.</summary>
    public const int MaxKept = 8;

    private readonly Lock _gate = new();
    private Secret[] _items;

    /// <summary>Creates the set with the secrets known now (a refresh token, a key) that are never dropped.</summary>
    /// <param name="fixedSecrets">The secrets that stay for the life of the set.</param>
    public LiveSecrets(IEnumerable<Secret>? fixedSecrets = null)
    {
        _items = fixedSecrets is null ? [] : [.. fixedSecrets];
        Fixed = _items.Length;
    }

    private int Fixed { get; }

    /// <inheritdoc />
    public int Count => _items.Length;

    /// <inheritdoc />
    public Secret this[int index] => _items[index];

    /// <summary>Adds a secret that was just minted. A secret already present is not added twice.</summary>
    /// <param name="secret">The secret.</param>
    public void Add(Secret secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        lock (_gate)
        {
            Secret[] current = _items;
            foreach (Secret s in current)
            {
                if (string.Equals(s.Value, secret.Value, StringComparison.Ordinal))
                {
                    return;
                }
            }

            // The array is replaced, never changed, so readers enumerate without taking the lock.
            int minted = current.Length - Fixed;
            Secret[] next = new Secret[minted >= MaxKept ? current.Length : current.Length + 1];
            Array.Copy(current, 0, next, 0, Fixed);
            int keep = Math.Min(minted, MaxKept - 1);
            Array.Copy(current, current.Length - keep, next, Fixed, keep);
            next[Fixed + keep] = secret;
            _items = next;
        }
    }

    /// <inheritdoc />
    public IEnumerator<Secret> GetEnumerator() => ((IEnumerable<Secret>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
