using System.Text.Json;

namespace OneLlm.TemplateDiff;

/// <summary>Reads the hand-written case file (see testdata/templating/curated.txt for the format).</summary>
internal static class CuratedFile
{
    public static List<Case> Read(string path)
    {
        var cases = new List<Case>();
        Case? cur = null;
        var text = new StringBuilder();
        bool inText = false;

        void Flush()
        {
            if (cur is null)
            {
                return;
            }

            cur.Text = text.ToString().TrimEnd('\n');
            cases.Add(cur);
            text.Clear();
            cur = null;
            inText = false;
        }

        foreach (string line in File.ReadAllLines(path))
        {
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                Flush();
                string[] parts = line.Substring(4).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                cur = new Case { Name = parts[0], DataJson = "{}" };
                foreach (string flag in parts.Skip(1).Select(f => f.Trim((char)91, (char)93)))
                {
                    if (flag == "missingkey=error")
                    {
                        cur.MissingError = true;
                    }
                    else if (flag == "noescape")
                    {
                        cur.NoEscape = true;
                    }
                    else
                    {
                        throw new InvalidDataException("unknown flag " + flag);
                    }
                }

                continue;
            }

            if (cur is null || (!inText && line.StartsWith('#')))
            {
                continue;
            }

            if (inText)
            {
                text.Append(line).Append('\n');
            }
            else if (line == "tmpl:")
            {
                inText = true;
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                cur.DataJson = line.Substring(6);
            }
            else if (line.StartsWith("embed ", StringComparison.Ordinal))
            {
                int colon = line.IndexOf(':', StringComparison.Ordinal);
                string name = line.Substring(6, colon - 6);
                using JsonDocument d = JsonDocument.Parse(line.Substring(colon + 1).Trim());
                cur.Embeds[name] = d.RootElement.GetString()!;
            }
            else if (line.Length > 0)
            {
                throw new InvalidDataException("unexpected line in case " + cur.Name + ": " + line);
            }
        }

        Flush();
        return cases;
    }
}
