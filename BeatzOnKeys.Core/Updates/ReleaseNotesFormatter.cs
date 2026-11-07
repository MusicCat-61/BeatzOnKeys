using System.Text;

namespace BeatzOnKeys.Core.Updates;

/// <summary>One rendered line of release notes.</summary>
public enum NoteBlockKind { Heading, Subheading, Bullet, Paragraph }

/// <summary>A run of text inside a block, optionally bold or monospaced.</summary>
public sealed record NoteRun(string Text, bool Bold = false, bool Code = false);

/// <summary>A single line of the notes, already stripped of its Markdown markers.</summary>
public sealed record NoteBlock(NoteBlockKind Kind, IReadOnlyList<NoteRun> Runs)
{
    public string Text => string.Concat(Runs.Select(run => run.Text));
}

/// <summary>
/// Turns a GitHub release body into blocks a small dialog can show. The body is Markdown written
/// for a browser: it has language sections, asset tables and code fences. A dialog needs the
/// section for the user's language, with headings, bullets and emphasis rendered rather than
/// printed as literal "##" and "**".
/// </summary>
public static class ReleaseNotesFormatter
{
    /// <summary>Headings that start a language section, mapped to the language code they serve.</summary>
    private static readonly Dictionary<string, string> SectionLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["english"] = "en",
        ["русский"] = "ru",
        ["russian"] = "ru"
    };

    /// <summary>Headings that are never prose: the asset tables live there.</summary>
    private static readonly string[] AssetHeadings = ["assets", "ассеты"];

    /// <summary>
    /// Picks the part of the notes written in <paramref name="languageCode"/>. When there are no
    /// language sections the whole body is used, so a short note still works.
    /// </summary>
    public static string SelectSection(string? notes, string languageCode)
    {
        if (string.IsNullOrWhiteSpace(notes)) { return string.Empty; }
        var lines = notes.Replace("\r\n", "\n").Split('\n');
        var sections = SplitSections(lines);
        if (sections.Count == 0) { return notes.Trim(); }

        var wanted = languageCode.StartsWith("ru", StringComparison.OrdinalIgnoreCase) ? "ru" : "en";
        foreach (var section in sections)
        {
            if (SectionLanguages.TryGetValue(section.Title, out var language) && language == wanted)
            {
                return section.Body.Trim();
            }
        }

        // No matching language: the first non-asset section is the closest readable answer.
        foreach (var section in sections)
        {
            if (!AssetHeadings.Contains(section.Title, StringComparer.OrdinalIgnoreCase))
            {
                return section.Body.Trim();
            }
        }

        return notes.Trim();
    }

    /// <summary>Parses Markdown prose into blocks, dropping tables, fences and rules.</summary>
    public static IReadOnlyList<NoteBlock> Parse(string? markdown)
    {
        var blocks = new List<NoteBlock>();
        if (string.IsNullOrWhiteSpace(markdown)) { return blocks; }

        var inFence = false;
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence || trimmed.Length == 0) { continue; }
            // Table rows carry download links and read badly in a narrow dialog.
            if (trimmed.StartsWith('|')) { continue; }
            if (trimmed is "---" or "***" or "___") { continue; }

            var kind = NoteBlockKind.Paragraph;
            var content = trimmed;
            if (trimmed.StartsWith("### ", StringComparison.Ordinal)) { kind = NoteBlockKind.Subheading; content = trimmed[4..]; }
            else if (trimmed.StartsWith("## ", StringComparison.Ordinal)) { kind = NoteBlockKind.Heading; content = trimmed[3..]; }
            else if (trimmed.StartsWith("# ", StringComparison.Ordinal)) { kind = NoteBlockKind.Heading; content = trimmed[2..]; }
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal)) { kind = NoteBlockKind.Bullet; content = trimmed[2..]; }
            else if (trimmed.StartsWith("* ", StringComparison.Ordinal)) { kind = NoteBlockKind.Bullet; content = trimmed[2..]; }

            var runs = ParseRuns(content);
            if (runs.Count > 0) { blocks.Add(new NoteBlock(kind, runs)); }
        }

        return blocks;
    }

    /// <summary>Splits one line into runs, honouring **bold**, `code` and [label](url).</summary>
    public static IReadOnlyList<NoteRun> ParseRuns(string text)
    {
        var runs = new List<NoteRun>();
        var plain = new StringBuilder();
        var index = 0;

        void Flush()
        {
            if (plain.Length > 0)
            {
                runs.Add(new NoteRun(plain.ToString()));
                plain.Clear();
            }
        }

        while (index < text.Length)
        {
            var rest = text.AsSpan(index);
            if (rest.StartsWith("**"))
            {
                var end = text.IndexOf("**", index + 2, StringComparison.Ordinal);
                if (end > index + 2)
                {
                    Flush();
                    runs.Add(new NoteRun(text[(index + 2)..end], Bold: true));
                    index = end + 2;
                    continue;
                }
            }

            if (text[index] == '`')
            {
                var end = text.IndexOf('`', index + 1);
                if (end > index + 1)
                {
                    Flush();
                    runs.Add(new NoteRun(text[(index + 1)..end], Code: true));
                    index = end + 1;
                    continue;
                }
            }

            if (text[index] == '[')
            {
                var close = text.IndexOf(']', index + 1);
                if (close > index + 1 && close + 1 < text.Length && text[close + 1] == '(')
                {
                    var paren = text.IndexOf(')', close + 2);
                    if (paren > close + 2)
                    {
                        // A label reads fine; the raw URL in a dialog does not.
                        plain.Append(text[(index + 1)..close]);
                        index = paren + 1;
                        continue;
                    }
                }
            }

            plain.Append(text[index]);
            index++;
        }

        Flush();
        return runs;
    }

    private static List<(string Title, string Body)> SplitSections(IReadOnlyList<string> lines)
    {
        var sections = new List<(string, string)>();
        string? title = null;
        var body = new StringBuilder();

        foreach (var raw in lines)
        {
            var trimmed = raw.TrimStart();
            if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                if (title is not null) { sections.Add((title, body.ToString())); }
                title = trimmed[3..].Trim();
                body.Clear();
                continue;
            }

            if (title is not null) { body.Append(raw).Append('\n'); }
        }

        if (title is not null) { sections.Add((title, body.ToString())); }
        return sections;
    }
}
