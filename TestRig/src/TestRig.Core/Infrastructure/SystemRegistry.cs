using Microsoft.Win32;
using TestRig.Core.Abstractions;

namespace TestRig.Core.Infrastructure;

/// <summary>
/// The real registry, read only.
/// </summary>
/// <remarks>
/// <para>
/// There is no write path here and there must never be one. See <see cref="IRegistry"/>: the
/// state this reads is shared with the developer's own client, cannot be isolated, and is
/// REPORTED rather than restored. A writer would turn an honest report into the write the
/// save rules forbid.
/// </para>
/// <para>
/// AOT note: <c>Microsoft.Win32.Registry</c> is a plain Win32 wrapper with no reflection in
/// it, so it trims and compiles ahead of time without a rooting descriptor.
/// </para>
/// </remarks>
public sealed class SystemRegistry : IRegistry
{
    /// <summary>A shared instance. The type is stateless.</summary>
    public static readonly SystemRegistry Instance = new();

    public IReadOnlyList<KeyValuePair<string, string>>? TryReadValues(string keyPath)
    {
        // The rig is Windows-only by construction (it drives a Windows game through
        // CreateProcessW and CreateDesktopW), but this is a real guard rather than a
        // suppression: "no registry here" is exactly the same answer as "the key could not be
        // read", and the report already knows how to say that.
        if (!OperatingSystem.IsWindows()) return null;

        if (string.IsNullOrWhiteSpace(keyPath)) return null;

        var (root, subKey) = Split(keyPath);
        if (root is null) return null;

        try
        {
            using var key = root.OpenSubKey(subKey, writable: false);
            if (key is null) return null;

            var values = new List<KeyValuePair<string, string>>();
            foreach (var name in key.GetValueNames())
            {
                values.Add(new KeyValuePair<string, string>(name, Render(key.GetValue(name))));
            }

            // Sorted here rather than at the call site, so two snapshots of the same key are
            // comparable line by line whatever order the registry enumerated them in.
            values.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
            return values;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException
                                       or IOException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Renders one value the way the snapshot stores it.
    /// </summary>
    /// <remarks>
    /// A byte array becomes <c>bytes[N]</c> rather than its contents (RESET-138). Unity stores
    /// every PlayerPrefs string as REG_BINARY, so without this the snapshot would be a wall of
    /// hex that nothing reads and everything has to diff.
    /// </remarks>
    private static string Render(object? value) => value switch
    {
        null => string.Empty,
        byte[] bytes => $"bytes[{bytes.Length}]",
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>
    /// Splits <c>HKCU:\Software\...</c> into a hive and a sub-key path.
    /// </summary>
    /// <remarks>
    /// The <c>HKCU:</c> spelling is PowerShell's provider syntax and is what every rig
    /// document and every message uses, so it is parsed here rather than changed everywhere
    /// else. The long forms are accepted too, because a reader who reaches for one is not
    /// wrong.
    /// </remarks>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static (RegistryKey? Root, string SubKey) Split(string keyPath)
    {
        var path = keyPath.Replace('/', '\\').TrimStart('\\');
        var cut = path.IndexOf('\\');
        var hive = (cut < 0 ? path : path[..cut]).TrimEnd(':');
        var rest = cut < 0 ? string.Empty : path[(cut + 1)..];

        RegistryKey? root = hive.ToUpperInvariant() switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => Registry.CurrentUser,
            "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
            "HKCR" or "HKEY_CLASSES_ROOT" => Registry.ClassesRoot,
            "HKU" or "HKEY_USERS" => Registry.Users,
            _ => null,
        };

        return (root, rest);
    }
}
