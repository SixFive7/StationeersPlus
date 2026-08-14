using System.Text;

namespace TestRig.Playtest.Evidence;

/// <summary>File-name safe, stable, lower case.</summary>
/// <remarks>
///     Evidence folders and files are named from check names, reader names and select paths,
///     any of which can carry a slash or a colon, so this is what stops a check name from
///     producing an unwritable path.
/// </remarks>
public static class Slug
{
    /// <summary>Longest slug produced. Beyond this a name is truncated and re-trimmed.</summary>
    public const int MaxLength = 60;

    /// <summary>The slug for text with nothing slug-able in it.</summary>
    public const string Empty = "unnamed";

    public static string Of(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Empty;

        var builder = new StringBuilder(text.Length);
        var pendingSeparator = false;
        foreach (var c in text)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                if (pendingSeparator && builder.Length > 0) builder.Append('-');
                pendingSeparator = false;
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                pendingSeparator = true;
            }
        }

        var slug = builder.ToString();
        if (slug.Length == 0) return Empty;
        if (slug.Length > MaxLength) slug = slug[..MaxLength].Trim('-');
        return slug.Length == 0 ? Empty : slug;
    }
}
