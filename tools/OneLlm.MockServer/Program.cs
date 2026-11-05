using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Hosting;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OneLlm.Conformance;

// A local HTTP server that answers from recorded fixtures, so benchmarks measure client code and not the network.
//
//   onellm-mockserver --fixtures <file-or-directory> [--port 5099]
//
// Every exchange of every fixture file is available. A request is answered with the first exchange whose method and path (including the
// query) match; request bodies and headers are not compared, because benchmark requests differ in size. Exchanges are reusable. A
// streamed response is written chunk by chunk, with a flush after each, the way a provider streams.

string? fixtures = null;
bool synthetic = false;
int port = 5099;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--fixtures" && i + 1 < args.Length)
    {
        fixtures = args[++i];
    }
    else if (args[i] == "--synthetic")
    {
        synthetic = true;
    }
    else if (args[i] == "--port" && i + 1 < args.Length)
    {
        port = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
    }
}

if (fixtures is null && !synthetic)
{
    await Console.Error.WriteLineAsync("usage: onellm-mockserver (--fixtures <file-or-directory> | --synthetic) [--port 5099]");
    return 2;
}

string[] files = fixtures is null ? [] : Directory.Exists(fixtures)
    ? [.. Directory.EnumerateFiles(fixtures, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal)]
    : [fixtures];
var exchanges = new List<RecordedExchange>();
foreach (string file in files)
{
    exchanges.AddRange(RecordedFixture.Load(file).Exchanges);
}

WebApplicationBuilder builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
WebApplication app = builder.Build();

app.Run(async context =>
{
    if (synthetic && await SyntheticServer.TryHandleAsync(context))
    {
        return;
    }

    string path = context.Request.Path + context.Request.QueryString;
    RecordedExchange? match = exchanges.FirstOrDefault(e =>
        string.Equals(e.Request.Method, context.Request.Method, StringComparison.OrdinalIgnoreCase) && e.Request.Path == path);
    if (match is null)
    {
        context.Response.StatusCode = 404;
        await context.Response.WriteAsync($"no recorded exchange for {context.Request.Method} {path}");
        return;
    }

    // Drain the request body so keep-alive connections stay usable.
    await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);

    RecordedResponse response = match.Response;
    context.Response.StatusCode = response.Status;
    foreach ((string name, string value) in response.Headers)
    {
        context.Response.Headers[name] = value;
    }

    if (response.Sse is { } chunks)
    {
        context.Response.ContentType ??= "text/event-stream";
        foreach (string chunk in chunks)
        {
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(chunk), context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }

        return;
    }

    await context.Response.Body.WriteAsync(response.Body.ToWireBytes(), context.RequestAborted);
});

await Console.Out.WriteLineAsync($"serving {exchanges.Count} recorded exchange(s) on http://127.0.0.1:{port}");
await app.RunAsync();
return 0;
