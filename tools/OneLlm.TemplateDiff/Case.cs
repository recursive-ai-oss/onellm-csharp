using System.Text.Json;

namespace OneLlm.TemplateDiff;

/// <summary>One differential case: template text, JSON data, options and embeddable texts.</summary>
internal sealed class Case
{
    public string Text = "";
    public string DataJson = "null";
    public bool MissingError;
    public bool NoEscape;
    public string Name = "";
    public Dictionary<string, string> Embeds = new();

    public string ToJsonLine()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("t", Text);
            w.WritePropertyName("data");
            using (JsonDocument d = JsonDocument.Parse(DataJson))
            {
                d.RootElement.WriteTo(w);
            }

            w.WriteString("missingKey", MissingError ? "error" : "default");
            if (NoEscape)
            {
                w.WriteBoolean("escape", false);
            }

            w.WriteStartObject("embeds");
            foreach (var kv in Embeds)
            {
                w.WriteString(kv.Key, kv.Value);
            }

            w.WriteEndObject();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static Case FromJson(JsonElement e)
    {
        var c = new Case { Text = e.GetProperty("t").GetString()!, DataJson = e.GetProperty("data").GetRawText() };
        c.MissingError = e.TryGetProperty("missingKey", out var mk) && mk.GetString() == "error";
        c.NoEscape = e.TryGetProperty("escape", out var es) && !es.GetBoolean();
        if (e.TryGetProperty("name", out var nm))
        {
            c.Name = nm.GetString()!;
        }

        if (e.TryGetProperty("embeds", out var em))
        {
            foreach (JsonProperty p in em.EnumerateObject())
            {
                c.Embeds[p.Name] = p.Value.GetString()!;
            }
        }

        return c;
    }
}

/// <summary>Our engine's result in the same shape the Go driver reports.</summary>
internal readonly record struct Outcome(bool Ok, string Out, string Stage, string Err);

internal static class OursRunner
{
    public static Outcome Run(Case c)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(c.DataJson);
            var render = new OneLlm.Templating.TemplateRenderOptions
            {
                MissingKey = c.MissingError ? OneLlm.Templating.MissingKeyMode.Error : OneLlm.Templating.MissingKeyMode.Default,
                EmbedSources = new OneLlm.Templating.IEmbedSource[] { new OneLlm.Templating.PromptEmbedSource(c.Embeds) },
            };
            var parse = new OneLlm.Templating.TemplateParseOptions { EscapeCodeRegions = !c.NoEscape };
            string s = OneLlm.Templating.Template.Render(c.Text, doc.RootElement, parse, render);
            return new Outcome(true, s, "", "");
        }
        catch (OneLlm.Templating.TemplateException ex)
        {
            return new Outcome(false, "", ex.Kind == OneLlm.Templating.TemplateErrorKind.Parse ? "parse" : "exec", ex.Message);
        }
    }
}
