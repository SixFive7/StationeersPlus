namespace TestRig.Cli.Parsing;

/// <summary>What an option carries.</summary>
public enum OptionKind
{
    /// <summary>Free text.</summary>
    Text,

    /// <summary>A whole number.</summary>
    Number,

    /// <summary>Present or absent. Also accepts an explicit <c>=true</c> / <c>:false</c>.</summary>
    Flag,

    /// <summary>One of a fixed set, matched case-insensitively and echoed in canonical casing.</summary>
    Choice,
}

/// <summary>
/// One command-line option, declared once and used by the parser, the surface and the
/// per-verb applicability check.
/// </summary>
/// <remarks>
/// <para>
/// The PowerShell launcher declared every option in one <c>param()</c> block shared by all
/// twenty-two verbs, so nothing knew which options a verb actually reads. Three measured
/// consequences: <c>--dry-run</c> bound on every verb and was honoured by <c>reset</c>
/// alone; <c>-ForceGameplayInput</c> and <c>-SeedMods</c> were <c>[bool]</c> rather than
/// switches, so they could not be written without a value; and a second bare argument bound
/// to <c>-Mod</c> on every verb, which made <c>testrig status server</c> report the whole
/// rig in silence.
/// </para>
/// <para>
/// Declaring the option set as data fixes all three in one place: <see cref="Verbs.VerbTable"/>
/// names the options each verb consumes, the parser rejects the rest, and the surface is
/// generated rather than hand-maintained.
/// </para>
/// </remarks>
/// <param name="Name">Canonical kebab-case name, without leading dashes.</param>
/// <param name="Kind">What the option carries.</param>
/// <param name="Default">The default, rendered for the surface. Empty means "unset".</param>
/// <param name="Help">One line, shown by the surface.</param>
/// <param name="Choices">The permitted values when <see cref="Kind"/> is <see cref="OptionKind.Choice"/>.</param>
/// <param name="DefaultsTrue">
/// A flag that starts on and is turned off with the <c>--no-</c> form. This is the fix for
/// the two <c>[bool]</c> parameters: <c>--no-seed-mods</c> instead of <c>-SeedMods $false</c>.
/// </param>
public sealed record OptionSpec(
    string Name,
    OptionKind Kind,
    string Default,
    string Help,
    IReadOnlyList<string>? Choices = null,
    bool DefaultsTrue = false)
{
    /// <summary>The lookup key: lower-cased with separators removed.</summary>
    /// <remarks>
    /// So <c>--save-name</c>, <c>-SaveName</c>, <c>--savename</c> and <c>-savename</c> are
    /// one option. The PowerShell binder was case-insensitive and this keeps that, which
    /// matters because every refusal string and every document written before the port
    /// spells options in PascalCase with a single dash.
    /// </remarks>
    public string Key { get; } = Normalize(Name);

    /// <summary>How the option is written on a command line.</summary>
    public string Display => "--" + Name;

    /// <summary>Strips dashes and underscores and lower-cases. See <see cref="Key"/>.</summary>
    public static string Normalize(string raw)
    {
        var chars = new char[raw.Length];
        var n = 0;
        foreach (var c in raw)
        {
            if (c is '-' or '_') continue;
            chars[n++] = char.ToLowerInvariant(c);
        }

        return new string(chars, 0, n);
    }
}
