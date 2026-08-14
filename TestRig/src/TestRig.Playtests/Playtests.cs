using TestRig.Playtest;
using TestRig.Playtest.Model;

namespace TestRig.Playtests;

/// <summary>
///     Every check compiled into this build.
/// </summary>
/// <remarks>
///     Reading this property is what forces the module to load, which is what runs each
///     check file's own <c>[ModuleInitializer]</c>. Without a touch on a type defined here,
///     an assembly nothing else references may never be loaded at all.
/// </remarks>
public static class Playtests
{
    public static IReadOnlyList<IPlaytestCheck> All => PlaytestCheckRegistry.Registered;
}
