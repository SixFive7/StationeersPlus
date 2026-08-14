using System.Text;
using System.Xml.Linq;
using TestRig.Core.Abstractions;

namespace TestRig.Core.Rig;

/// <summary>One entry in a <c>modconfig.xml</c>, in document order.</summary>
/// <param name="Kind">The element name: <c>Core</c>, <c>Local</c> or <c>Workshop</c>.</param>
/// <param name="Enabled">
/// An equality test against the literal string <c>true</c>. <c>Enabled="True"</c> and
/// <c>Enabled="1"</c> therefore read as DISABLED, which matches the game and matters
/// because the baseline stores this file byte for byte (COMMON-044).
/// </param>
public sealed record ModConfigEntry(string Kind, bool Enabled, string Path, string WorkshopId)
{
    public static ModConfigEntry Local(string path) => new("Local", true, path, "");
}

/// <summary>
/// The one reader and the one writer for <c>modconfig.xml</c>.
/// </summary>
/// <remarks>
/// There used to be three writers producing three formats: the server's local-entry
/// appender string-replaced <c>&lt;/ModConfig&gt;</c>, the server's full sync wrote one
/// shape and the client half wrote another. The baseline stores every
/// <c>modconfig.xml</c> by CONTENT and restores it byte for byte, so whichever action last
/// touched a file decided whether a clean rig read as clean. One writer.
/// </remarks>
public static class ModConfig
{
    /// <summary>
    /// Parses a config into ordered entries. A missing file, or one with no
    /// <c>ModConfig</c> root, is an empty set rather than a throw.
    /// </summary>
    /// <remarks>
    /// Every entry keeps its Enabled value rather than being filtered here. A caller that
    /// wants only the enabled ones filters; a caller rewriting a DEVELOPER'S file in place
    /// must not drop the disabled ones, because re-enabling one afterwards is a normal
    /// thing to do (COMMON-044).
    /// </remarks>
    public static IReadOnlyList<ModConfigEntry> Read(IFileSystem fs, string path)
    {
        var result = new List<ModConfigEntry>();
        if (!fs.FileExists(path)) return result;

        XDocument doc;
        try
        {
            doc = XDocument.Parse(fs.ReadAllText(path));
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            // A malformed config degrades to empty, exactly as the PowerShell's missing-root
            // branch did. Throwing here would make a single bad character stop a deploy.
            return result;
        }

        var root = doc.Root;
        if (root is null || root.Name.LocalName != "ModConfig") return result;

        // Element children only, in document order, because the order carries load-order
        // intent (COMMON-043).
        foreach (var node in root.Elements())
        {
            result.Add(new ModConfigEntry(
                node.Name.LocalName,
                string.Equals((string?)node.Attribute("Enabled") ?? "", "true", StringComparison.Ordinal),
                ChildValue(node, "Path"),
                ChildValue(node, "WorkshopId")));
        }

        return result;
    }

    /// <summary>The <c>Value</c> attribute of a named child element, or the empty string.</summary>
    private static string ChildValue(XElement parent, string child) =>
        (string?)parent.Elements().FirstOrDefault(e => e.Name.LocalName == child)?.Attribute("Value") ?? "";

    /// <summary>
    /// Renders the canonical file. Byte-for-byte stable, because the baseline stores it.
    /// </summary>
    /// <remarks>
    /// A <c>Core</c> block is always emitted first and any Core entry in the input is
    /// DROPPED (COMMON-046). A port that round-tripped entries faithfully would emit two.
    /// </remarks>
    public static string Render(IEnumerable<ModConfigEntry>? entries)
    {
        var sb = new StringBuilder();

        // CRLF explicitly, not Environment.NewLine: this content is compared byte for byte
        // against a stored baseline, so it cannot depend on the platform it was written on.
        const string nl = "\r\n";

        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>").Append(nl);
        sb.Append("<ModConfig xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">").Append(nl);
        sb.Append("  <Core Enabled=\"true\">").Append(nl);
        sb.Append("    <Path />").Append(nl);
        sb.Append("  </Core>").Append(nl);

        foreach (var entry in entries ?? [])
        {
            // A null in the input is skipped rather than throwing (COMMON-047).
            if (entry is null) continue;
            if (string.Equals(entry.Kind, "Core", StringComparison.Ordinal)) continue;

            var kind = string.IsNullOrEmpty(entry.Kind) ? "Local" : entry.Kind;
            var enabled = entry.Enabled ? "true" : "false";

            sb.Append("  <").Append(kind).Append(" Enabled=\"").Append(enabled).Append("\">").Append(nl);
            sb.Append("    <Path Value=\"").Append(Escape(entry.Path)).Append("\" />").Append(nl);
            if (!string.IsNullOrEmpty(entry.WorkshopId))
            {
                sb.Append("    <WorkshopId Value=\"").Append(Escape(entry.WorkshopId)).Append("\" />").Append(nl);
            }
            sb.Append("  </").Append(kind).Append('>').Append(nl);
        }

        sb.Append("</ModConfig>").Append(nl);
        return sb.ToString();
    }

    /// <summary>Writes the canonical file, creating the parent directory on demand.</summary>
    /// <remarks>
    /// UTF-8 with no byte order mark, pinned by <see cref="IFileSystem.WriteAllText"/>
    /// (COMMON-052). The PowerShell used <c>Set-Content -Encoding utf8</c>, which is
    /// utf8NoBOM under pwsh 7 and something else under Windows PowerShell 5.1, on a file
    /// the baseline stores byte for byte.
    /// </remarks>
    public static void Write(IFileSystem fs, string path, IEnumerable<ModConfigEntry>? entries)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) fs.CreateDirectory(dir);
        fs.WriteAllText(path, Render(entries));
    }

    /// <summary>
    /// Ensures an enabled <c>Local</c> entry pointing at a folder. Idempotent.
    /// </summary>
    /// <returns>True when an entry was added, false when a matching one was already there.</returns>
    /// <remarks>
    /// The match trims trailing separators and compares case-insensitively, because the
    /// same folder arrives with and without a trailing backslash depending on which
    /// caller built the path.
    /// </remarks>
    public static bool AddLocalEntry(IFileSystem fs, string path, string localModDir)
    {
        var entries = Read(fs, path).ToList();

        foreach (var entry in entries)
        {
            if (!string.Equals(entry.Kind, "Local", StringComparison.Ordinal)) continue;
            if (string.IsNullOrEmpty(entry.Path)) continue;
            if (entry.Path.TrimEnd('\\', '/').Equals(localModDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        entries.Add(ModConfigEntry.Local(localModDir));
        Write(fs, path, entries);
        return true;
    }

    /// <summary>
    /// XML attribute escaping, matching <c>SecurityElement.Escape</c>.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than called so the exact five substitutions are visible: this
    /// output is compared byte for byte against a stored baseline, and an escaper that
    /// escapes one character more or fewer silently invalidates every stored copy.
    /// </remarks>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                case '&': sb.Append("&amp;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
