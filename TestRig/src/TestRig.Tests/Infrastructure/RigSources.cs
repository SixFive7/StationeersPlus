namespace TestRig.Tests.Infrastructure;

/// <summary>
/// Locates the rig's source tree from inside the test host.
/// </summary>
/// <remarks>
/// Found by walking up from the test assembly until TestRig.slnx appears, rather than by
/// an MSBuild property or a relative hop count. Both alternatives break the moment an
/// output path changes, and they break by making a scan find nothing, which reads as a
/// pass.
/// </remarks>
internal static class RigSources
{
    public static string SrcRoot { get; } = Find();

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TestRig.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find TestRig.slnx above {AppContext.BaseDirectory}, so the source tree cannot be " +
            "scanned. Any test that depends on this must fail rather than quietly scan nothing.");
    }
}
