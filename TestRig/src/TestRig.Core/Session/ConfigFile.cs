using System.Text.RegularExpressions;
using TestRig.Core.Abstractions;

namespace TestRig.Core.Session;

/// <summary>
/// Reading and blanking single values in a BepInEx config file.
/// </summary>
/// <remarks>
/// Blanking rewrites one line and leaves the rest of the file exactly as it was, comments
/// included. Rewriting from a model would silently drop every comment BepInEx wrote, and
/// those comments are the only documentation a plugin's settings have.
/// </remarks>
public static class ConfigFile
{
    /// <summary>One value, or null when the file or the setting is absent. Never writes.</summary>
    public static string? GetSetting(IFileSystem fs, string path, string setting)
    {
        if (!fs.FileExists(path)) return null;

        IReadOnlyList<string> lines;
        try { lines = fs.ReadLines(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        var pattern = new Regex(@"^\s*" + Regex.Escape(setting) + @"\s*=\s*(.*)$", RegexOptions.CultureInvariant);
        foreach (var line in lines)
        {
            if (Regex.IsMatch(line, @"^\s*#")) continue;
            var match = pattern.Match(line);
            if (match.Success) return match.Groups[1].Value.Trim();
        }
        return null;
    }

    /// <summary>Blanks the FIRST non-comment match, preserving everything else. False when not found.</summary>
    public static bool BlankSetting(IFileSystem fs, string path, string setting)
    {
        if (!fs.FileExists(path)) return false;

        IReadOnlyList<string> lines;
        try { lines = fs.ReadLines(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }

        var pattern = new Regex(@"^(\s*" + Regex.Escape(setting) + @"\s*=).*$", RegexOptions.CultureInvariant);
        var hit = false;
        var output = new List<string>(lines.Count);

        foreach (var line in lines)
        {
            if (!hit && !Regex.IsMatch(line, @"^\s*#"))
            {
                var match = pattern.Match(line);
                if (match.Success)
                {
                    hit = true;
                    output.Add(match.Groups[1].Value + " ");
                    continue;
                }
            }
            output.Add(line);
        }

        if (!hit) return false;
        fs.WriteAllText(path, string.Join(FieldText.NewLine, output) + FieldText.NewLine);
        return true;
    }
}

/// <summary>
/// Points an instance at its OWN user-data root.
/// </summary>
/// <remarks>
/// This is the single thing standing between a driven session and the developer's tier-1
/// save folder, and it lives in one place because two callers need it and must not drift:
/// provisioning writes it when an instance is built, and the reset re-writes it after
/// re-copying BepInEx/config, which WIPES it. Two copies of this is how one of them
/// quietly stops matching the other and an instance ends up writing worlds into the
/// developer's saves.
///
/// SavePathOverride moves StationSaveUtils.DefaultPath itself, which is the only lever
/// that also separates modconfig.xml. Do NOT reach for the launch flag "-settings
/// SavePath" instead: it moves the save tree but not DefaultPath, so StationeersLaunchPad
/// scans an empty SavePath\mods\, finds nothing, and rewrites the DEVELOPER'S SHARED
/// modconfig.xml with every Local entry deleted. Observed on a first boot: five local mod
/// entries silently removed, and nothing warned.
///
/// A failure to write it is fatal for a host and merely loud for a client. That asymmetry
/// is the whole point: a joining client reads a world the server owns and writes none of
/// its own, while a host CREATES a world, and a host with no redirect creates it inside
/// the developer's saves. An unknown role is treated as a host, because the expensive
/// mistake is assuming a host is a client.
/// </remarks>
public static class SavePathOverride
{
    public const string ConfigLeaf = "stationeers.launchpad.cfg";
    public const string SettingName = "SavePathOverride";

    public static string ConfigPath(string bepInExDir) => Path.Combine(bepInExDir, "config", ConfigLeaf);

    /// <summary>Writes the redirect. Returns false when it could not be written for a client.</summary>
    /// <exception cref="RigRefusalException">The role is host (or unknown) and the config is missing.</exception>
    public static bool Write(
        IFileSystem fs,
        IOutput output,
        string bepInExDir,
        string userDataDir,
        string instanceRole,
        string instanceName = "",
        string context = "Provision")
    {
        var who = string.IsNullOrEmpty(instanceName) ? "" : $"[{instanceName}] ";
        var config = ConfigPath(bepInExDir);

        if (!fs.FileExists(config))
        {
            var why = $"{who}{ConfigLeaf} not found at {config}, so {SettingName} could not be written and this "
                      + "instance has NO separate save root: everything it writes lands in the developer's own "
                      + "user-data folder, which is tier 1 and off-limits. Launch the instance once to generate the "
                      + "config, then rebuild it: testrig create --target <name> --force --as <id>.";

            if (!string.Equals(instanceRole, "client", StringComparison.OrdinalIgnoreCase))
            {
                throw new RigRefusalException(
                    RigRefusalKind.Refused,
                    why + "\nRefusing to leave a host without the redirect: a host creates a world, and that world "
                        + "would be created inside the developer's saves.");
            }

            output.Line(OutputLevel.Warning,
                why + "\nTreat this as a stop, not a note: do not start this instance until the redirect is in place.");
            return false;
        }

        var line = $"{SettingName} = {userDataDir}";
        var lines = fs.ReadLines(config).ToList();
        var replaced = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (Regex.IsMatch(lines[i], @"^" + SettingName + @"\s*="))
            {
                lines[i] = line;
                replaced = true;
            }
        }
        if (!replaced) lines.Add(line);

        fs.WriteAllText(config, string.Join(FieldText.NewLine, lines) + FieldText.NewLine);
        output.Line(OutputLevel.Detail, $"[{context}] {SettingName} -> {userDataDir}");
        return true;
    }

    /// <summary>Reads the redirect back, or null when it is absent.</summary>
    public static string? Read(IFileSystem fs, string bepInExDir) =>
        ConfigFile.GetSetting(fs, ConfigPath(bepInExDir), SettingName);
}
