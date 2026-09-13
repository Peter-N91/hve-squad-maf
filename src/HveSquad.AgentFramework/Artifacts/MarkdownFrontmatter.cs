namespace HveSquad.AgentFramework.Artifacts;

/// <summary>Splits a markdown document into its YAML frontmatter block and body.</summary>
public static class MarkdownFrontmatter
{
    private const string Fence = "---";

    /// <summary>
    /// Returns the raw YAML frontmatter and the remaining body. When the document has no
    /// frontmatter, <paramref name="yaml"/> is empty and the whole document is the body.
    /// </summary>
    public static void Split(string document, out string yaml, out string body)
    {
        ArgumentNullException.ThrowIfNull(document);

        var text = document.TrimStart('\uFEFF');
        using var reader = new StringReader(text);

        var first = reader.ReadLine();
        if (first?.Trim() != Fence)
        {
            yaml = string.Empty;
            body = text;
            return;
        }

        var yamlLines = new List<string>();
        string? line;
        var closed = false;

        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Trim() == Fence)
            {
                closed = true;
                break;
            }

            yamlLines.Add(line);
        }

        if (!closed)
        {
            // An unterminated fence is not frontmatter; treat the document as body-only.
            yaml = string.Empty;
            body = text;
            return;
        }

        yaml = string.Join('\n', yamlLines);
        body = reader.ReadToEnd().TrimStart('\r', '\n');
    }
}
