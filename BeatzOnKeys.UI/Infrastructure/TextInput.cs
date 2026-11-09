namespace BeatzOnKeys.UI.Infrastructure;

/// <summary>
/// One rule for every text field in the window: a single line, at most 200 characters, and no line
/// breaks. A pasted block — a link with a stray newline, a title copied from a page — must not put
/// a line break into a field that is meant to hold one line. Trimming is deliberately left out:
/// a trailing space is removed only where the value is used, otherwise typing "two words" would
/// lose the space the moment it is typed.
/// </summary>
public static class TextInput
{
    /// <summary>The longest value any field accepts, matching the MaxLength set in XAML.</summary>
    public const int MaxLength = 200;

    /// <summary>Flattens line breaks into single spaces and caps the length.</summary>
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) { return string.Empty; }
        var flat = value.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');
        return flat.Length <= MaxLength ? flat : flat[..MaxLength];
    }
}