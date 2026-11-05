namespace OneLlm.Auth;

/// <summary>
/// AWS access keys for signing requests (Signature Version 4). Credentials are given explicitly or read from the three standard environment
/// variables; configuration files, instance metadata and role assumption are not used.
/// </summary>
public sealed class AwsCredentials
{
    /// <summary>The environment variable that holds the access key id.</summary>
    public const string AccessKeyIdVariable = "AWS_ACCESS_KEY_ID";

    /// <summary>The environment variable that holds the secret access key.</summary>
    public const string SecretAccessKeyVariable = "AWS_SECRET_ACCESS_KEY";

    /// <summary>The environment variable that holds the session token of temporary credentials (optional).</summary>
    public const string SessionTokenVariable = "AWS_SESSION_TOKEN";

    /// <summary>Creates credentials.</summary>
    /// <param name="accessKeyId">The access key id (not secret; it appears in the <c>Authorization</c> header).</param>
    /// <param name="secretAccessKey">The secret access key.</param>
    /// <param name="sessionToken">The session token of temporary credentials; null for long-lived keys.</param>
    /// <exception cref="ArgumentException">The key id or the secret is empty.</exception>
    public AwsCredentials(string accessKeyId, Secret secretAccessKey, Secret? sessionToken = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessKeyId);
        ArgumentNullException.ThrowIfNull(secretAccessKey);
        if (secretAccessKey.IsEmpty)
        {
            throw new ArgumentException("The AWS secret access key is empty.", nameof(secretAccessKey));
        }

        AccessKeyId = accessKeyId.Trim();
        SecretAccessKey = secretAccessKey;
        SessionToken = sessionToken is { IsEmpty: false } ? sessionToken : null;
    }

    /// <summary>The access key id.</summary>
    public string AccessKeyId { get; }

    /// <summary>The secret access key.</summary>
    public Secret SecretAccessKey { get; }

    /// <summary>The session token, or null.</summary>
    public Secret? SessionToken { get; }

    /// <summary>The secrets to scrub from error text.</summary>
    public IReadOnlyList<Secret> Secrets => SessionToken is null ? [SecretAccessKey] : [SecretAccessKey, SessionToken];

    /// <summary>Reads <c>AWS_ACCESS_KEY_ID</c>, <c>AWS_SECRET_ACCESS_KEY</c> and (optionally) <c>AWS_SESSION_TOKEN</c>.</summary>
    /// <param name="lookup">Reads a variable. The process environment when null; tests and hosts that keep their own settings pass a function.</param>
    /// <returns>The credentials.</returns>
    /// <exception cref="AuthException">The key id or the secret is not set. The message names the variables, never their values.</exception>
    public static AwsCredentials FromEnvironment(Func<string, string?>? lookup = null)
    {
        lookup ??= Environment.GetEnvironmentVariable;
        string? id = lookup(AccessKeyIdVariable);
        string? secret = lookup(SecretAccessKeyVariable);
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret))
        {
            throw new AuthException("bedrock", $"AWS credentials were not found: set {AccessKeyIdVariable} and {SecretAccessKeyVariable} (and {SessionTokenVariable} for temporary credentials), or pass AwsCredentials.", null);
        }

        string? token = lookup(SessionTokenVariable);
        return new AwsCredentials(id, new Secret(secret), string.IsNullOrWhiteSpace(token) ? null : new Secret(token));
    }
}
