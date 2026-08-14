using TestRig.Core.Session;
using Xunit;

namespace TestRig.Tests.Session;

/// <summary>
/// The shared key=value format. Ported from rig-lock.tests.ps1 section 12 (file format).
/// </summary>
public sealed class FieldTextTests
{
    [Fact]
    public void CommentsAndBlankLinesAreSkipped()
    {
        var fields = FieldText.Parse("# a comment\n\n   \nowner=abc12345\n# trailing comment\n");

        Assert.Equal(1, fields.Count);
        Assert.Equal("abc12345", fields.Get(LockFields.Owner));
        Assert.False(fields.Contains("# a comment"));
    }

    [Fact]
    public void IndentedCommentsAreStillComments()
    {
        var fields = FieldText.Parse("   # owner=notreally\nowner=real0001\n");

        Assert.Equal("real0001", fields.Get(LockFields.Owner));
        Assert.Equal(1, fields.Count);
    }

    [Fact]
    public void BothLineEndingsParse()
    {
        var crlf = FieldText.Parse("owner=abc\r\npurpose=probe\r\n");
        var lf = FieldText.Parse("owner=abc\npurpose=probe\n");

        Assert.Equal("abc", crlf.Get("owner"));
        Assert.Equal("probe", crlf.Get("purpose"));
        Assert.Equal(crlf.Get("owner"), lf.Get("owner"));
        Assert.Equal(crlf.Get("purpose"), lf.Get("purpose"));
    }

    [Fact]
    public void OnlyTheFirstEqualsSplits()
    {
        var fields = FieldText.Parse("purpose=a=b=c\n");

        Assert.Equal("a=b=c", fields.Get("purpose"));
    }

    [Fact]
    public void ALineStartingWithEqualsIsSkipped()
    {
        var fields = FieldText.Parse("=orphan\nowner=abc\n");

        Assert.Equal(1, fields.Count);
        Assert.Equal("abc", fields.Get("owner"));
    }

    [Fact]
    public void ALineWithNoEqualsIsSkipped()
    {
        var fields = FieldText.Parse("this is not a field\nowner=abc\n");

        Assert.Equal(1, fields.Count);
        Assert.True(fields.Contains("owner"));
    }

    [Fact]
    public void KeysAndValuesAreTrimmed()
    {
        var fields = FieldText.Parse("  owner  =   abc12345   \n");

        Assert.Equal("abc12345", fields.Get("owner"));
        Assert.True(fields.Contains("owner"));
    }

    [Fact]
    public void LaterKeysOverwriteEarlierOnes()
    {
        var fields = FieldText.Parse("owner=first\nowner=second\n");

        Assert.Equal("second", fields.Get("owner"));
        Assert.Equal(1, fields.Count);
    }

    [Fact]
    public void KeysAreCaseInsensitiveAsPowerShellOrderedHashtablesAre()
    {
        var fields = FieldText.Parse("Owner=abc\n");

        Assert.True(fields.Contains("owner"));
        Assert.Equal("abc", fields.Get("OWNER"));
    }

    [Fact]
    public void FieldOrderIsPreservedOnWrite()
    {
        var fields = new FieldText();
        fields.Set("owner", "abc");
        fields.Set("purpose", "probe");
        fields.Set("ttl_minutes", "10");

        var rendered = fields.Render(["# header"]);

        Assert.Equal(
            "# header\r\nowner=abc\r\npurpose=probe\r\nttl_minutes=10\r\n",
            rendered);
    }

    [Fact]
    public void SettingAnExistingKeyKeepsItsPosition()
    {
        var fields = FieldText.Parse("owner=abc\npurpose=probe\nhost=PC\n");
        fields.Set("purpose", "changed");

        Assert.Equal(["owner", "purpose", "host"], fields.Keys);
        Assert.Equal("changed", fields.Get("purpose"));
    }

    [Fact]
    public void SettingANewKeyAppendsIt()
    {
        var fields = FieldText.Parse("owner=abc\n");
        fields.Set("idle_ceiling_minutes", "60");

        Assert.Equal(["owner", "idle_ceiling_minutes"], fields.Keys);
    }

    [Fact]
    public void ASixFieldRoundTripSurvives()
    {
        var original = new FieldText();
        original.Set("owner", "2620c93c");
        original.Set("purpose", "probe = with = equals");
        original.Set("acquired_at", "2026-08-13T23:11:14Z");
        original.Set("refreshed_at", "2026-08-13T23:11:14Z");
        original.Set("ttl_minutes", "10");
        original.Set("host", "PC657");

        var round = FieldText.Parse(original.Render(["# h1", "# h2"]));

        Assert.Equal(6, round.Count);
        Assert.Equal("2620c93c", round.Get("owner"));
        Assert.Equal("probe = with = equals", round.Get("purpose"));
        Assert.Equal("PC657", round.Get("host"));
        Assert.Equal(original.Keys, round.Keys);
    }

    [Fact]
    public void EmptyAndNullTextParseToNothing()
    {
        Assert.Equal(0, FieldText.Parse("").Count);
        Assert.Equal(0, FieldText.Parse(null).Count);
        Assert.Equal(0, FieldText.Parse("   \n\n").Count);
    }

    [Fact]
    public void AnEmptyValueIsARealValueNotAMissingKey()
    {
        var fields = FieldText.Parse("worlds=\n");

        Assert.True(fields.Contains("worlds"));
        Assert.Equal("", fields.Get("worlds"));
    }

    [Fact]
    public void CloneDoesNotAliasTheOriginal()
    {
        var fields = FieldText.Parse("owner=abc\n");
        var clone = fields.Clone();
        clone.Set("owner", "changed");
        clone.Set("extra", "value");

        Assert.Equal("abc", fields.Get("owner"));
        Assert.False(fields.Contains("extra"));
        Assert.Equal("changed", clone.Get("owner"));
    }

    [Fact]
    public void GetOrEmptyNeverReturnsNull()
    {
        var fields = new FieldText();

        Assert.Equal("", fields.GetOrEmpty("nothing"));
        Assert.Null(fields.Get("nothing"));
    }

    [Fact]
    public void TimestampsRoundTripThroughTheOneFormat()
    {
        var when = new DateTimeOffset(2026, 8, 14, 23, 11, 14, TimeSpan.Zero);
        var stamp = RigTime.Stamp(when);

        Assert.Equal("2026-08-14T23:11:14Z", stamp);
        Assert.Equal(when, RigTime.TryParse(stamp));
    }

    [Fact]
    public void AnOffsetLessTimestampIsReadAsUtc()
    {
        Assert.Equal(
            new DateTimeOffset(2026, 8, 14, 23, 11, 14, TimeSpan.Zero),
            RigTime.TryParse("2026-08-14T23:11:14"));
    }

    [Fact]
    public void AnUnparseableTimestampIsNull()
    {
        Assert.Null(RigTime.TryParse("not a date"));
        Assert.Null(RigTime.TryParse(""));
        Assert.Null(RigTime.TryParse(null));
    }
}
