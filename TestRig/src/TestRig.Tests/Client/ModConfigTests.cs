using TestRig.Core.Rig;
using TestRig.Tests.Session.Fakes;
using Xunit;

namespace TestRig.Tests.Client;

/// <summary>
/// The one <c>modconfig.xml</c> reader and the one writer.
/// </summary>
/// <remarks>
/// There used to be three writers producing three formats, and the baseline stores this file
/// BY CONTENT and restores it byte for byte, so whichever action last touched a file decided
/// whether a clean rig read as clean.
/// </remarks>
public sealed class ModConfigTests
{
    private const string Path = @"C:\rig\modconfig.xml";

    private static FakeFileSystem WithConfig(string xml)
    {
        var fs = new FakeFileSystem();
        fs.AddFile(Path, xml);
        return fs;
    }

    // ---- reading -----------------------------------------------------------

    [Fact]
    public void AMissingFileAndARootlessDocumentBothReadAsEmpty()
    {
        Assert.Empty(ModConfig.Read(new FakeFileSystem(), Path));
        Assert.Empty(ModConfig.Read(WithConfig("<NotModConfig><Local /></NotModConfig>"), Path));
    }

    [Fact]
    public void AMalformedFileDegradesToEmptyRatherThanStoppingADeploy()
    {
        Assert.Empty(ModConfig.Read(WithConfig("<ModConfig><Local Enabled=\"true\"></ModConfig>"), Path));
    }

    [Fact]
    public void DocumentOrderIsPreservedBecauseItCarriesLoadOrderIntent()
    {
        var fs = WithConfig(
            """
            <ModConfig>
              <Core Enabled="true"><Path /></Core>
              <Workshop Enabled="true"><Path Value="C:\w\1" /><WorkshopId Value="1" /></Workshop>
              <Local Enabled="true"><Path Value="C:\l\A" /></Local>
              <Local Enabled="false"><Path Value="C:\l\B" /></Local>
            </ModConfig>
            """);

        var entries = ModConfig.Read(fs, Path);
        Assert.Equal(["Core", "Workshop", "Local", "Local"], entries.Select(static e => e.Kind));
        Assert.Equal(@"C:\l\A", entries[2].Path);
        Assert.Equal("1", entries[1].WorkshopId);
    }

    [Fact]
    public void DisabledEntriesAreKeptRatherThanFilteredOut()
    {
        // A caller rewriting a DEVELOPER'S file in place must not drop them: re-enabling one
        // afterwards is a normal thing to do.
        var fs = WithConfig("""<ModConfig><Local Enabled="false"><Path Value="C:\off" /></Local></ModConfig>""");
        var entry = Assert.Single(ModConfig.Read(fs, Path));
        Assert.False(entry.Enabled);
        Assert.Equal(@"C:\off", entry.Path);
    }

    [Fact]
    public void EnabledIsComparedAgainstTheLiteralLowercaseTrue()
    {
        // Enabled="True" and Enabled="1" read as DISABLED, which matches the game. Byte-for-byte
        // baseline storage makes it observable, so it cannot be quietly relaxed.
        var fs = WithConfig(
            """
            <ModConfig>
              <Local Enabled="true"><Path Value="C:\a" /></Local>
              <Local Enabled="True"><Path Value="C:\b" /></Local>
              <Local Enabled="1"><Path Value="C:\c" /></Local>
              <Local><Path Value="C:\d" /></Local>
            </ModConfig>
            """);

        Assert.Equal([true, false, false, false], ModConfig.Read(fs, Path).Select(static e => e.Enabled));
    }

    [Fact]
    public void AnEntryWithNoPathOrWorkshopIdReadsAsEmptyStringsAndNotNulls()
    {
        var fs = WithConfig("""<ModConfig><Local Enabled="true" /></ModConfig>""");
        var entry = Assert.Single(ModConfig.Read(fs, Path));
        Assert.Equal("", entry.Path);
        Assert.Equal("", entry.WorkshopId);
    }

    // ---- writing -----------------------------------------------------------

    [Fact]
    public void TheCanonicalFileOpensWithTheDeclarationAndTheTwoNamespaceDeclarations()
    {
        var text = ModConfig.Render([]);
        var lines = text.Split("\r\n");

        Assert.Equal("<?xml version=\"1.0\" encoding=\"utf-8\"?>", lines[0]);
        Assert.Contains("xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\"", lines[1], StringComparison.Ordinal);
        Assert.Contains("xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"", lines[1], StringComparison.Ordinal);
        Assert.Equal("  <Core Enabled=\"true\">", lines[2]);
        Assert.Equal("    <Path />", lines[3]);
        Assert.Equal("  </Core>", lines[4]);
        Assert.Equal("</ModConfig>", lines[5]);
        Assert.EndsWith("</ModConfig>\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LineEndingsAreCrlfWhateverTheHostPlatformIs()
    {
        // The content is compared byte for byte against a stored baseline, so it cannot depend
        // on the platform it was written on.
        var text = ModConfig.Render([]);
        Assert.DoesNotContain(text.Replace("\r\n", ""), "\n", StringComparison.Ordinal);
    }

    [Fact]
    public void ACoreEntrySuppliedByTheCallerIsDroppedSoTheFileNeverHasTwo()
    {
        // A port that round-tripped entries faithfully would emit two Core blocks.
        var text = ModConfig.Render([new ModConfigEntry("Core", true, @"C:\ignored", "")]);
        Assert.Equal(1, CountOf(text, "<Core "));
    }

    [Fact]
    public void ANullEntryIsSkippedRatherThanThrowing()
    {
        var entries = new List<ModConfigEntry> { null!, ModConfigEntry.Local(@"C:\a") };
        var text = ModConfig.Render(entries);
        Assert.Contains(@"C:\a", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyKindDefaultsToLocalAndEnabledRendersAsTheLiteral()
    {
        var text = ModConfig.Render([new ModConfigEntry("", false, @"C:\x", "")]);
        Assert.Contains("<Local Enabled=\"false\">", text, StringComparison.Ordinal);
        Assert.Contains("</Local>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AWorkshopIdIsEmittedOnlyWhenItIsNonEmpty()
    {
        Assert.DoesNotContain("WorkshopId", ModConfig.Render([ModConfigEntry.Local(@"C:\a")]), StringComparison.Ordinal);
        Assert.Contains("<WorkshopId Value=\"77\" />",
            ModConfig.Render([new ModConfigEntry("Workshop", true, @"C:\w", "77")]), StringComparison.Ordinal);
    }

    [Fact]
    public void AttributeValuesAreXmlEscapedInBothPlaces()
    {
        var text = ModConfig.Render([new ModConfigEntry("Workshop", true, "a<b>&\"c\"'d", "x&y")]);
        Assert.Contains("Value=\"a&lt;b&gt;&amp;&quot;c&quot;&apos;d\"", text, StringComparison.Ordinal);
        Assert.Contains("Value=\"x&amp;y\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEscaperCoversExactlyTheFiveCharactersAndNothingElse()
    {
        Assert.Equal("&lt;&gt;&quot;&apos;&amp;", ModConfig.Escape("<>\"'&"));
        Assert.Equal(@"C:\Program Files\x", ModConfig.Escape(@"C:\Program Files\x"));
        Assert.Equal("", ModConfig.Escape(null));
    }

    [Fact]
    public void AWrittenFileRoundTripsThroughTheReaderUnchanged()
    {
        var fs = new FakeFileSystem();
        fs.AddDirectory(@"C:\rig");

        var entries = new List<ModConfigEntry>
        {
            new("Workshop", true, @"C:\w\1", "1"),
            new("Local", false, @"C:\l\A", ""),
        };
        ModConfig.Write(fs, Path, entries);

        var read = ModConfig.Read(fs, Path);
        Assert.Equal(["Core", "Workshop", "Local"], read.Select(static e => e.Kind));
        Assert.Equal(entries[0], read[1]);
        Assert.Equal(entries[1], read[2]);
    }

    [Fact]
    public void WritingCreatesTheParentDirectoryOnDemand()
    {
        var fs = new FakeFileSystem();
        ModConfig.Write(fs, @"C:\brand\new\modconfig.xml", []);
        Assert.True(fs.FileExists(@"C:\brand\new\modconfig.xml"));
    }

    [Fact]
    public void TheWrittenBytesCarryNoByteOrderMark()
    {
        // Pinned rather than inherited from a shell: Set-Content -Encoding utf8 is utf8NoBOM
        // under pwsh 7 and something else under Windows PowerShell 5.1, on a file the baseline
        // stores byte for byte.
        var fs = new FakeFileSystem();
        fs.AddDirectory(@"C:\rig");
        ModConfig.Write(fs, Path, []);

        var bytes = fs.ReadAllBytes(Path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    // ---- adding a local entry ----------------------------------------------

    [Fact]
    public void AddingALocalEntryIsIdempotentAcrossTrailingSeparatorsAndCase()
    {
        var fs = new FakeFileSystem();
        fs.AddDirectory(@"C:\rig");

        Assert.True(ModConfig.AddLocalEntry(fs, Path, @"C:\mods\Local_Foo"));
        Assert.False(ModConfig.AddLocalEntry(fs, Path, @"C:\mods\Local_Foo\"));
        Assert.False(ModConfig.AddLocalEntry(fs, Path, @"c:\MODS\local_foo"));

        Assert.Equal(1, CountOf(fs.ReadAllText(Path), "Local_Foo"));
    }

    [Fact]
    public void AMissingFileIsCreatedWithCorePlusTheOneNewEntry()
    {
        var fs = new FakeFileSystem();
        Assert.True(ModConfig.AddLocalEntry(fs, @"C:\fresh\modconfig.xml", @"C:\mods\Local_Bar"));

        var entries = ModConfig.Read(fs, @"C:\fresh\modconfig.xml");
        Assert.Equal(["Core", "Local"], entries.Select(static e => e.Kind));
        Assert.Equal(@"C:\mods\Local_Bar", entries[1].Path);
        Assert.True(entries[1].Enabled);
    }

    [Fact]
    public void AddingPreservesEveryExistingEntryIncludingTheDisabledOnes()
    {
        var fs = WithConfig(
            """
            <ModConfig>
              <Core Enabled="true"><Path /></Core>
              <Workshop Enabled="true"><Path Value="C:\w\1" /><WorkshopId Value="1" /></Workshop>
              <Local Enabled="false"><Path Value="C:\l\Off" /></Local>
            </ModConfig>
            """);

        Assert.True(ModConfig.AddLocalEntry(fs, Path, @"C:\l\New"));

        var entries = ModConfig.Read(fs, Path);
        Assert.Equal(["Core", "Workshop", "Local", "Local"], entries.Select(static e => e.Kind));
        Assert.False(entries[2].Enabled);
        Assert.Equal(@"C:\l\Off", entries[2].Path);
        Assert.Equal(@"C:\l\New", entries[3].Path);
    }

    [Fact]
    public void ADifferentKindAtTheSamePathIsNotTreatedAsAMatch()
    {
        var fs = WithConfig("""<ModConfig><Workshop Enabled="true"><Path Value="C:\same" /></Workshop></ModConfig>""");
        Assert.True(ModConfig.AddLocalEntry(fs, Path, @"C:\same"));
    }

    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
