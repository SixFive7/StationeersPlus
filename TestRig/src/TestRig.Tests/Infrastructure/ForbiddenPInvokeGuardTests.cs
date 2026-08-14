using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace TestRig.Tests.Infrastructure;

/// <summary>
/// The focus constraint, enforced against the source tree instead of by convention.
/// </summary>
/// <remarks>
/// No code in TestRig/src/ may focus, raise or activate a window. Instances run on a
/// Win32 desktop created with CreateDesktopW and never switched to. Measured, sampling
/// the foreground every 3 seconds for two minutes: SW_SHOWNOACTIVATE alone lost 40 focus
/// steals out of 40 samples, and the separate desktop lost 0 out of 55.
///
/// A comment saying so is not enough. The rule has to survive an agent six months from
/// now who needs a window raised for what looks like a good reason, so it is a test that
/// fails the build.
///
/// The scan runs over code with comments and literals removed, because the names have to
/// be writable in prose: this file lists them, and DesktopProcessLauncher.cs explains why
/// they are absent. A mention is documentation. An identifier in code is an import.
/// </remarks>
public sealed class ForbiddenPInvokeGuardTests
{
    /// <summary>
    /// The imports that would let this tool take the developer's foreground.
    /// </summary>
    /// <remarks>
    /// SwitchDesktop is the one the whole mechanism turns on. The rest are the managed
    /// and unmanaged routes to the same outcome: SetForegroundWindow and
    /// BringWindowToTop raise a window, ShowWindow and SetWindowPos can activate one,
    /// AttachThreadInput and SetActiveWindow reach the foreground through the input
    /// queue, and SetThreadDesktop would move this thread onto the desktop instances run
    /// on, which is the first half of switching to it.
    /// </remarks>
    private static readonly string[] Forbidden =
    [
        "SwitchDesktop",
        "SetForegroundWindow",
        "ShowWindow",
        "SetWindowPos",
        "AttachThreadInput",
        "BringWindowToTop",
        "SetActiveWindow",
        "SetThreadDesktop",
    ];

    /// <summary>
    /// The one file allowed to carry the names as string literals: this one.
    /// </summary>
    /// <remarks>
    /// The list above has to exist somewhere, and the EntryPoint scan below matches
    /// string literals by design. Excluding the guard itself is the only exception, and
    /// it is safe because the guard has no P/Invoke of its own to hide.
    /// </remarks>
    private const string GuardFileName = "ForbiddenPInvokeGuardTests.cs";

    [Fact]
    public void NoForbiddenWindowOrDesktopCallAppearsAnywhereInTheSourceTree()
    {
        var offences = new List<string>();
        var scanned = 0;

        foreach (var file in EnumerateSourceFiles())
        {
            scanned++;
            var stripped = StripCommentsAndLiterals(File.ReadAllText(file));
            var relative = Path.GetRelativePath(RigSources.SrcRoot, file);

            foreach (var name in Forbidden)
            {
                foreach (Match match in Regex.Matches(stripped, $@"\b{name}\b"))
                {
                    var line = stripped.Take(match.Index).Count(c => c == '\n') + 1;
                    offences.Add($"{relative}:{line}  {name}");
                }
            }
        }

        // A guard that scanned nothing would pass forever.
        Assert.True(scanned > 5, $"the forbidden-import scan found only {scanned} source files under {RigSources.SrcRoot}");
        Assert.Empty(offences);
    }

    [Fact]
    public void NoDllImportNamesAForbiddenEntryPoint()
    {
        // Second angle, on the raw text: an EntryPoint value is a string literal, so the
        // scan above strips it away. Between the two, neither the declared method name
        // nor the exported name can slip through.
        var offences = new List<string>();
        var scanned = 0;

        foreach (var file in EnumerateSourceFiles())
        {
            if (string.Equals(Path.GetFileName(file), GuardFileName, StringComparison.Ordinal)) continue;

            scanned++;
            var text = File.ReadAllText(file);
            var relative = Path.GetRelativePath(RigSources.SrcRoot, file);

            foreach (Match match in Regex.Matches(text, @"EntryPoint\s*=\s*""(?<name>[A-Za-z0-9_]+)"""))
            {
                var entry = match.Groups["name"].Value;
                var bare = entry.Length > 1 && (entry[^1] == 'W' || entry[^1] == 'A') ? entry[..^1] : entry;

                if (Array.Exists(Forbidden, f => string.Equals(f, entry, StringComparison.Ordinal))
                    || Array.Exists(Forbidden, f => string.Equals(f, bare, StringComparison.Ordinal)))
                {
                    offences.Add($"{relative}  EntryPoint = \"{entry}\"");
                }
            }
        }

        Assert.True(scanned > 5, $"the EntryPoint scan found only {scanned} source files under {RigSources.SrcRoot}");
        Assert.Empty(offences);
    }

    [Fact]
    public void TheDesktopLauncherStillCarriesTheThreeImportsTheMechanismNeeds()
    {
        // The other half of the constraint: the desktop mechanism has to still be there.
        // Deleting the P/Invoke and falling back to Process.Start would pass every
        // prohibition above and cost the developer their foreground on the next run,
        // because ProcessStartInfo cannot express lpDesktop or wShowWindow.
        var launcher = File.ReadAllText(Path.Combine(
            RigSources.SrcRoot, "TestRig.Core", "Infrastructure", "Win32", "DesktopProcessLauncher.cs"));

        Assert.Contains("CreateDesktopW", launcher, StringComparison.Ordinal);
        Assert.Contains("CreateProcessW", launcher, StringComparison.Ordinal);
        Assert.Contains("lpDesktop", launcher, StringComparison.Ordinal);
        Assert.Contains("wShowWindow", launcher, StringComparison.Ordinal);
    }

    // ---- the stripper, and proof that it works ---------------------------

    [Fact]
    public void TheStripperKeepsCodeAndDropsCommentsAndLiterals()
    {
        // Without this, a bug in the stripper would turn the guard above into a test that
        // cannot fail.
        const string forbidden = "Switch" + "Desktop";

        Assert.Contains(forbidden, StripCommentsAndLiterals($"static extern bool {forbidden}(IntPtr h);"), StringComparison.Ordinal);
        Assert.DoesNotContain(forbidden, StripCommentsAndLiterals($"// never import {forbidden}"), StringComparison.Ordinal);
        Assert.DoesNotContain(forbidden, StripCommentsAndLiterals($"/// <summary>no {forbidden}</summary>"), StringComparison.Ordinal);
        Assert.DoesNotContain(forbidden, StripCommentsAndLiterals($"/* {forbidden} is banned */"), StringComparison.Ordinal);
        Assert.DoesNotContain(forbidden, StripCommentsAndLiterals($"var s = \"{forbidden}\";"), StringComparison.Ordinal);
        Assert.DoesNotContain(forbidden, StripCommentsAndLiterals($"var s = @\"{forbidden}\";"), StringComparison.Ordinal);
        Assert.DoesNotContain(forbidden, StripCommentsAndLiterals($"var s = \"\"\"{forbidden}\"\"\";"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheStripperKeepsLineNumbersAlignedAndSurvivesAwkwardSyntax()
    {
        // A stripper that ate newlines would report every offence on the wrong line.
        var stripped = StripCommentsAndLiterals("line1();\n/* two\nthree */\nline4();\n");
        Assert.Equal(5, stripped.Split('\n').Length);
        Assert.Contains("line4", stripped.Split('\n')[3], StringComparison.Ordinal);

        // A quote inside a char literal must not open a string, or everything after it
        // would be swallowed and the guard would stop seeing code.
        Assert.Contains("Keep", StripCommentsAndLiterals("var q = '\"'; Keep();"), StringComparison.Ordinal);
        Assert.Contains("Keep", StripCommentsAndLiterals("var s = \"a\\\"b\"; Keep();"), StringComparison.Ordinal);
        Assert.Contains("Keep", StripCommentsAndLiterals("var s = @\"a\"\"b\"; Keep();"), StringComparison.Ordinal);
        Assert.Contains("Keep", StripCommentsAndLiterals("var s = \"\"\"a\"b\"\"\"; Keep();"), StringComparison.Ordinal);
        Assert.Contains("Keep", StripCommentsAndLiterals("var s = $@\"a\"\"b\"; Keep();"), StringComparison.Ordinal);
    }

    private static IEnumerable<string> EnumerateSourceFiles()
    {
        var options = new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.None,
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        };

        foreach (var file in Directory.EnumerateFiles(RigSources.SrcRoot, "*.cs", options))
        {
            var relative = Path.GetRelativePath(RigSources.SrcRoot, file);
            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // bin/ and obj/ are build output, including generated interop stubs, which are
            // derived from the sources this scan already covers.
            if (Array.Exists(segments, s => s is "bin" or "obj")) continue;

            yield return file;
        }
    }

    /// <summary>
    /// Removes comments, string literals and char literals, keeping newlines so line
    /// numbers still line up with the original.
    /// </summary>
    /// <remarks>
    /// Known limitation: the code inside an interpolation hole is removed with the rest
    /// of its literal. That cannot hide an import, which is a declaration and can never
    /// be inside one, and the EntryPoint scan covers the string side independently.
    /// </remarks>
    internal static string StripCommentsAndLiterals(string source)
    {
        var sb = new StringBuilder(source.Length);
        var i = 0;

        while (i < source.Length)
        {
            var c = source[i];

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    if (source[i] == '\n') sb.Append('\n');
                    i++;
                }

                i = Math.Min(i + 2, source.Length);
                continue;
            }

            if (c == '"' && i + 2 < source.Length && source[i + 1] == '"' && source[i + 2] == '"')
            {
                var opening = 0;
                while (i < source.Length && source[i] == '"')
                {
                    opening++;
                    i++;
                }

                while (i < source.Length)
                {
                    if (source[i] == '"')
                    {
                        var closing = 0;
                        while (i < source.Length && source[i] == '"')
                        {
                            closing++;
                            i++;
                        }

                        if (closing >= opening) break;
                        continue;
                    }

                    if (source[i] == '\n') sb.Append('\n');
                    i++;
                }

                continue;
            }

            // Verbatim: @"..." and the interpolated forms $@"..." and @$"...".
            if (c == '@' && (Next(source, i, 1) == '"' || (Next(source, i, 1) == '$' && Next(source, i, 2) == '"')))
            {
                i += Next(source, i, 1) == '"' ? 2 : 3;

                while (i < source.Length)
                {
                    if (source[i] == '"')
                    {
                        if (i + 1 < source.Length && source[i + 1] == '"')
                        {
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    if (source[i] == '\n') sb.Append('\n');
                    i++;
                }

                continue;
            }

            if (c == '"')
            {
                i++;
                while (i < source.Length)
                {
                    if (source[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (source[i] == '"')
                    {
                        i++;
                        break;
                    }

                    // Unterminated on this line. Bail rather than swallow the rest of the
                    // file, which would silently blind the scan.
                    if (source[i] == '\n') break;

                    i++;
                }

                continue;
            }

            if (c == '\'')
            {
                i++;
                while (i < source.Length)
                {
                    if (source[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (source[i] == '\'')
                    {
                        i++;
                        break;
                    }

                    if (source[i] == '\n') break;

                    i++;
                }

                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static char Next(string source, int index, int offset) =>
        index + offset < source.Length ? source[index + offset] : '\0';
}
