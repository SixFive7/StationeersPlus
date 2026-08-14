using System.Reflection;

[assembly: AssemblyTitle("TestRig")]
[assembly: AssemblyDescription("Developer tooling: the in-process half of the Stationeers test rig. One loopback HTTP control plane plus the scenario probe host, running in both the game client and the dedicated server.")]
[assembly: AssemblyProduct("TestRig")]

// Tied to the const, not to a literal. ScenarioRunner hardcoded "0.1.0.0" here and
// its assembly version drifted away from the version it reported over its own log
// lines. One source keeps them together.
[assembly: AssemblyVersion(TestRig.Plugin.PluginVersion)]
[assembly: AssemblyFileVersion(TestRig.Plugin.PluginVersion)]
