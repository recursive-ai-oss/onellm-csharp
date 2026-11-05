namespace OneLlm.Auth;

/// <summary>Shared checks and addresses for the Vertex AI deployments.</summary>
internal static class VertexNames
{
    /// <summary>Refuses a project or location that could change the host or path it is put into.</summary>
    public static void Require(string value, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        foreach (char c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':'))
            {
                throw new ArgumentException($"The {parameter} contains a character that is not allowed in a Google Cloud name.", parameter);
            }
        }
    }

    /// <summary>The address for a location: <c>{location}-aiplatform.googleapis.com</c>, or <c>aiplatform.googleapis.com</c> for <c>global</c> (Vertex AI locations page).</summary>
    public static Uri BaseAddress(Uri? configured, string location)
    {
        Uri address = configured ?? new Uri(location == "global" ? "https://aiplatform.googleapis.com/" : $"https://{location}-aiplatform.googleapis.com/");
        if (address.Scheme != Uri.UriSchemeHttps && !(address.Scheme == Uri.UriSchemeHttp && address.IsLoopback))
        {
            throw new ArgumentException("The Vertex AI address must be https (http is allowed only for loopback addresses).", nameof(configured));
        }

        return address.AbsoluteUri.EndsWith('/') ? address : new Uri(address.AbsoluteUri + "/");
    }
}
