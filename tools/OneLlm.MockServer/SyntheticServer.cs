using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Deterministic synthetic provider responses for benchmarks: Anthropic Messages, OpenAI Responses and Chat Completions, and Gemini
/// <c>generateContent</c>, each whole or as a server-sent-event stream. Shapes follow the vendors' published API references.
///
/// <para>Configure with <c>POST /_mock/config?events=2000&amp;bytes=8&amp;flushEvery=8</c>: a streamed answer carries <c>events</c> text deltas of
/// <c>bytes</c> characters each (a whole answer carries the same text in one piece). The first event is always flushed alone, so time to
/// first event is not hidden behind buffering; later events are flushed every <c>flushEvery</c> events.</para>
///
/// <para>Whether a request wants a stream is read from its body (<c>"stream":true</c>) or, for Gemini, from the method in the path. The
/// request body is read and discarded.</para>
/// </summary>
internal static class SyntheticServer
{
    private static readonly object Gate = new();
    private static Config s_config = new(2000, 8, 8, "", "");
    private static readonly Dictionary<string, byte[][]> Cache = new(StringComparer.Ordinal);

    private readonly record struct Config(int Events, int Bytes, int FlushEvery, string Tool, string Text);

    public static async Task<bool> TryHandleAsync(HttpContext context)
    {
        string path = context.Request.Path.Value ?? "";
        if (path == "/_mock/config")
        {
            lock (Gate)
            {
                Config c = s_config;
                c = c with
                {
                    Events = Read(context, "events", c.Events),
                    Bytes = Read(context, "bytes", c.Bytes),
                    FlushEvery = Math.Max(1, Read(context, "flushEvery", c.FlushEvery)),
                    // text=... fixes the answer's words (split into pieces of `bytes` characters); events then follows from its length.
                    Text = context.Request.Query.TryGetValue("text", out var words) ? words.ToString() : c.Text,
                    // tool=task_complete makes Chat Completions answers (whole and streamed) a call to that tool, so an agent run ends.
                    Tool = context.Request.Query.TryGetValue("tool", out var tool) ? tool.ToString() : c.Tool,
                };
                if (c.Text.Length > 0)
                {
                    c = c with { Events = (c.Text.Length + c.Bytes - 1) / c.Bytes };
                }

                s_config = c;
                Cache.Clear();
            }

            await context.Response.WriteAsync("ok");
            return true;
        }

        if (!HttpMethods.IsPost(context.Request.Method))
        {
            return false;
        }

        // Endpoints the smoke rehearsal uses (counts, embeddings, decisions, tool calls, structured output). Streams and plain turns fall
        // through to the generated answers below.
        if (!path.EndsWith(":streamGenerateContent", StringComparison.Ordinal))
        {
            using var raw = new MemoryStream();
            await context.Request.Body.CopyToAsync(raw, context.RequestAborted);
            string text = Encoding.UTF8.GetString(raw.GetBuffer(), 0, (int)raw.Length);
            if (!HasStreamTrue(raw.GetBuffer().AsSpan(0, (int)raw.Length)) && SmokeResponses.Respond(path, text) is { } json)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength = bytes.Length;
                await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
                return true;
            }

            context.Items["body"] = raw.ToArray();
        }

        string? kind = null;
        bool stream = false;
        if (path.EndsWith("/v1/messages", StringComparison.Ordinal))
        {
            kind = "anthropic";
        }
        else if (path.EndsWith("/v1/responses", StringComparison.Ordinal))
        {
            kind = "responses";
        }
        else if (path.EndsWith("/v1/chat/completions", StringComparison.Ordinal))
        {
            kind = "chat";
        }
        else if (path.Contains("/models/", StringComparison.Ordinal) && path.EndsWith(":generateContent", StringComparison.Ordinal))
        {
            kind = "gemini";
        }
        else if (path.Contains("/models/", StringComparison.Ordinal) && path.EndsWith(":streamGenerateContent", StringComparison.Ordinal))
        {
            kind = "gemini";
            stream = true;
        }

        if (kind is null)
        {
            return false;
        }

        // Read the whole body so the connection stays reusable; look for the stream flag as we go.
        using var body = new MemoryStream();
        if (context.Items.TryGetValue("body", out object? already) && already is byte[] saved)
        {
            body.Write(saved);
        }
        else
        {
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);
        }

        if (kind != "gemini")
        {
            stream = HasStreamTrue(body.GetBuffer().AsSpan(0, (int)body.Length));
        }

        Config c2;
        lock (Gate)
        {
            c2 = s_config;
        }

        string key = kind + (stream ? "/s" : "/w");
        byte[][] events;
        lock (Gate)
        {
            if (!Cache.TryGetValue(key, out events!))
            {
                events = stream ? BuildStream(kind, c2) : [BuildWhole(kind, c2)];
                Cache[key] = events;
            }
        }

        if (!stream)
        {
            context.Response.ContentType = "application/json";
            context.Response.ContentLength = events[0].Length;
            await context.Response.Body.WriteAsync(events[0], context.RequestAborted);
            return true;
        }

        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.Body.FlushAsync(context.RequestAborted);
        for (int i = 0; i < events.Length; i++)
        {
            await context.Response.Body.WriteAsync(events[i], context.RequestAborted);
            if (i == 0 || i % c2.FlushEvery == 0)
            {
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        }

        await context.Response.Body.FlushAsync(context.RequestAborted);
        return true;
    }

    private static int Read(HttpContext context, string name, int fallback) =>
        int.TryParse(context.Request.Query[name], NumberStyles.None, CultureInfo.InvariantCulture, out int v) ? v : fallback;

    private static bool HasStreamTrue(ReadOnlySpan<byte> body)
    {
        ReadOnlySpan<byte> a = "\"stream\":true"u8;
        ReadOnlySpan<byte> b = "\"stream\": true"u8;
        return body.IndexOf(a) >= 0 || body.IndexOf(b) >= 0;
    }

    private static string Delta(int i, int bytes)
    {
        string fixedText = s_config.Text;
        if (fixedText.Length > 0)
        {
            int start = i * bytes;
            return start >= fixedText.Length ? "" : fixedText.Substring(start, Math.Min(bytes, fixedText.Length - start));
        }

        var sb = new StringBuilder(bytes);
        for (int k = 0; k < bytes; k++)
        {
            sb.Append((char)('a' + ((i + k) % 26)));
        }

        return sb.ToString();
    }

    private static string AllText(Config c)
    {
        var sb = new StringBuilder(c.Events * c.Bytes);
        for (int i = 0; i < c.Events; i++)
        {
            sb.Append(Delta(i, c.Bytes));
        }

        return sb.ToString();
    }

    // The arguments of the tool call the mock makes: what task_complete takes.
    private const string ToolArgs = "{\\\"result\\\":\\\"done (mock)\\\",\\\"summary\\\":\\\"rehearsal\\\"}";

    private static string ToolWhole(string tool) =>
        $$$"""{"id":"chatcmpl-mock","object":"chat.completion","created":1700000000,"model":"mock-model","choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"{{{tool}}}","arguments":"{{{ToolArgs}}}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":25,"completion_tokens":10,"total_tokens":35}}""";

    private static byte[] U(string s) => Encoding.UTF8.GetBytes(s);

    private static byte[] BuildWhole(string kind, Config c)
    {
        string t = AllText(c);
        long outTokens = Math.Max(1, (long)c.Events * c.Bytes / 4);
        return U(kind switch
        {
            "anthropic" => $$$"""{"id":"msg_mock","type":"message","role":"assistant","model":"mock-model","content":[{"type":"text","text":"{{{t}}}"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":25,"output_tokens":{{{outTokens}}}}}""",
            "responses" => $$$"""{"id":"resp_mock","object":"response","created_at":1700000000,"status":"completed","error":null,"incomplete_details":null,"model":"mock-model","output":[{"id":"msg_1","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"{{{t}}}","annotations":[]}]}],"parallel_tool_calls":true,"tool_choice":"auto","tools":[],"usage":{"input_tokens":25,"input_tokens_details":{"cached_tokens":0},"output_tokens":{{{outTokens}}},"output_tokens_details":{"reasoning_tokens":0},"total_tokens":{{{25 + outTokens}}}}}""",
            "chat" when c.Tool.Length > 0 => ToolWhole(c.Tool),
            "chat" => $$$"""{"id":"chatcmpl-mock","object":"chat.completion","created":1700000000,"model":"mock-model","choices":[{"index":0,"message":{"role":"assistant","content":"{{{t}}}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":25,"completion_tokens":{{{outTokens}}},"total_tokens":{{{25 + outTokens}}}}}""",
            _ => $$$"""{"candidates":[{"content":{"role":"model","parts":[{"text":"{{{t}}}"}]},"finishReason":"STOP","index":0}],"usageMetadata":{"promptTokenCount":25,"candidatesTokenCount":{{{outTokens}}},"totalTokenCount":{{{25 + outTokens}}}},"modelVersion":"mock-model"}""",
        });
    }

    private static byte[][] BuildStream(string kind, Config c)
    {
        var list = new List<byte[]>();
        long outTokens = Math.Max(1, (long)c.Events * c.Bytes / 4);
        void Ev(string? name, string data) => list.Add(U((name is null ? "" : "event: " + name + "\n") + "data: " + data + "\n\n"));

        switch (kind)
        {
            case "anthropic":
                Ev("message_start", """{"type":"message_start","message":{"id":"msg_mock","type":"message","role":"assistant","model":"mock-model","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":25,"output_tokens":1}}}""");
                Ev("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""");
                for (int i = 0; i < c.Events; i++)
                {
                    Ev("content_block_delta", $$$"""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"{{{Delta(i, c.Bytes)}}}"}}""");
                }

                Ev("content_block_stop", """{"type":"content_block_stop","index":0}""");
                Ev("message_delta", $$$"""{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":{{{outTokens}}}}}""");
                Ev("message_stop", """{"type":"message_stop"}""");
                break;

            case "responses":
                int seq = 0;
                string shell = """{"id":"resp_mock","object":"response","created_at":1700000000,"status":"in_progress","model":"mock-model","output":[],"parallel_tool_calls":true,"tool_choice":"auto","tools":[]}""";
                Ev("response.created", $$$"""{"type":"response.created","sequence_number":{{{seq++}}},"response":{{{shell}}}}""");
                Ev("response.in_progress", $$$"""{"type":"response.in_progress","sequence_number":{{{seq++}}},"response":{{{shell}}}}""");
                Ev("response.output_item.added", $$$"""{"type":"response.output_item.added","sequence_number":{{{seq++}}},"output_index":0,"item":{"id":"msg_1","type":"message","status":"in_progress","role":"assistant","content":[]}}""");
                Ev("response.content_part.added", $$$"""{"type":"response.content_part.added","sequence_number":{{{seq++}}},"item_id":"msg_1","output_index":0,"content_index":0,"part":{"type":"output_text","text":"","annotations":[]}}""");
                for (int i = 0; i < c.Events; i++)
                {
                    Ev("response.output_text.delta", $$$"""{"type":"response.output_text.delta","sequence_number":{{{seq++}}},"item_id":"msg_1","output_index":0,"content_index":0,"delta":"{{{Delta(i, c.Bytes)}}}"}""");
                }

                string t = AllText(c);
                Ev("response.output_text.done", $$$"""{"type":"response.output_text.done","sequence_number":{{{seq++}}},"item_id":"msg_1","output_index":0,"content_index":0,"text":"{{{t}}}"}""");
                Ev("response.content_part.done", $$$"""{"type":"response.content_part.done","sequence_number":{{{seq++}}},"item_id":"msg_1","output_index":0,"content_index":0,"part":{"type":"output_text","text":"{{{t}}}","annotations":[]}}""");
                Ev("response.output_item.done", $$$"""{"type":"response.output_item.done","sequence_number":{{{seq++}}},"output_index":0,"item":{"id":"msg_1","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"{{{t}}}","annotations":[]}]}}""");
                string usageJson = $$$"""{"input_tokens":25,"input_tokens_details":{"cached_tokens":0},"output_tokens":{{{outTokens}}},"output_tokens_details":{"reasoning_tokens":0},"total_tokens":{{{25 + outTokens}}}}""";
                string done = $$$"""{"id":"resp_mock","object":"response","created_at":1700000000,"status":"completed","model":"mock-model","output":[{"id":"msg_1","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"{{{t}}}","annotations":[]}]}],"parallel_tool_calls":true,"tool_choice":"auto","tools":[],"usage":{{{usageJson}}}}""";
                Ev("response.completed", $$$"""{"type":"response.completed","sequence_number":{{{seq++}}},"response":{{{done}}}}""");
                break;

            case "chat" when c.Tool.Length > 0:
                Ev(null, $$$"""{"id":"chatcmpl-mock","object":"chat.completion.chunk","created":1700000000,"model":"mock-model","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"{{{c.Tool}}}","arguments":""}}]},"finish_reason":null}]}""");
                Ev(null, $$$"""{"id":"chatcmpl-mock","object":"chat.completion.chunk","created":1700000000,"model":"mock-model","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{{{ToolArgs}}}"}}]},"finish_reason":null}]}""");
                Ev(null, """{"id":"chatcmpl-mock","object":"chat.completion.chunk","created":1700000000,"model":"mock-model","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""");
                Ev(null, """{"id":"chatcmpl-mock","object":"chat.completion.chunk","created":1700000000,"model":"mock-model","choices":[],"usage":{"prompt_tokens":25,"completion_tokens":10,"total_tokens":35}}""");
                list.Add(U("data: [DONE]\n\n"));
                break;

            case "chat":
                Ev(null, """{"id":"chatcmpl-mock","object":"chat.completion.chunk","created":1700000000,"model":"mock-model","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}""");
                for (int i = 0; i < c.Events; i++)
                {
                    Ev(null, $$$"""{"id":"chatcmpl-mock","object":"chat.completion.chunk","created":1700000000,"model":"mock-model","choices":[{"index":0,"delta":{"content":"{{{Delta(i, c.Bytes)}}}"},"finish_reason":null}]}""");
                }

                Ev(null, """{"id":"chatcmpl-mock","object":"chat.completion.chunk","created":1700000000,"model":"mock-model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""");
                Ev(null, $$$"""{"id":"chatcmpl-mock","object":"chat.completion.chunk","created":1700000000,"model":"mock-model","choices":[],"usage":{"prompt_tokens":25,"completion_tokens":{{{outTokens}}},"total_tokens":{{{25 + outTokens}}}}}""");
                list.Add(U("data: [DONE]\n\n"));
                break;

            default:
                for (int i = 0; i < c.Events; i++)
                {
                    bool last = i == c.Events - 1;
                    string fin = last ? ",\"finishReason\":\"STOP\"" : "";
                    string usage = last ? $$$""","usageMetadata":{"promptTokenCount":25,"candidatesTokenCount":{{{outTokens}}},"totalTokenCount":{{{25 + outTokens}}}}""" : "";
                    Ev(null, $$$"""{"candidates":[{"content":{"role":"model","parts":[{"text":"{{{Delta(i, c.Bytes)}}}"}]},"index":0{{{fin}}}}]{{{usage}}},"modelVersion":"mock-model"}""");
                }

                break;
        }

        return [.. list];
    }
}
