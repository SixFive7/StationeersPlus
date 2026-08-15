using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using TestRig.Core.Abstractions;
using TestRig.Playtest.Model;

namespace TestRig.Playtest.Attestation;

/// <summary>What was attested about one instance.</summary>
/// <param name="Instance">The instance.</param>
/// <param name="Stamp">Its provision stamp, as written by the launcher.</param>
/// <param name="DeployedPath">Where the mod's assembly is inside that instance.</param>
/// <param name="DeployedSha256">The deployed file's content hash.</param>
/// <param name="ConfigEntryCount">How many config entries the LIVE process reports for the guid.</param>
public sealed record InstanceAttestation(
    string Instance,
    string Stamp,
    string DeployedPath,
    string DeployedSha256,
    int ConfigEntryCount);

/// <summary>The whole attestation report, written into the bundle on success.</summary>
public sealed record AttestationReport(
    ModIdentity Mod,
    string BuildPath,
    long BuildBytes,
    string BuildLastWriteUtc,
    string BuildSha256,
    IReadOnlyList<InstanceAttestation> Instances)
{
    public string ToJson()
    {
        var instances = new JsonArray();
        foreach (var instance in Instances)
        {
            instances.Add((JsonNode)new JsonObject
            {
                ["instance"] = instance.Instance,
                ["stamp"] = PlaytestJson.TryParse(instance.Stamp) ?? JsonValue.Create(instance.Stamp),
                ["deployed"] = instance.DeployedPath,
                ["deployedSha256"] = instance.DeployedSha256,
                ["configEntryCount"] = instance.ConfigEntryCount,
            });
        }

        var obj = new JsonObject
        {
            ["mod"] = Mod.ModName,
            ["guid"] = Mod.Guid,
            ["derivedFrom"] = "the check's own source location",
            ["buildUnderTest"] = string.Create(CultureInfo.InvariantCulture, $"{BuildPath} ({BuildBytes} bytes, {BuildLastWriteUtc})"),
            ["buildSha256"] = BuildSha256,
            ["instances"] = instances,
        };

        return PlaytestJson.Write(obj);
    }
}

/// <summary>
///     Proves that the processes under test are running the build under test.
/// </summary>
/// <remarks>
///     <para>
///     A live run once nearly measured a stale seeded assembly and was saved by luck, which
///     is why a check that never attests cannot report a pass. Four independent things are
///     checked, because each alone can be satisfied by a stale rig:
///     </para>
///     <list type="number">
///     <item>the build under test is on disk, at the path derived from the check's location;</item>
///     <item>each instance has a readable provision stamp, so the tree is one this launcher built;</item>
///     <item>the deployed assembly's CONTENT HASH equals the build's;</item>
///     <item>the live process reports configuration for the mod's guid.</item>
///     </list>
///     <para>
///     <b>Defect P-07.</b> Step 3 compared file LENGTH only, while its own documentation
///     claimed a content comparison ("matches the build under test by length and write time";
///     write time was formatted into the report and never compared anywhere). A same-length
///     different build attested cleanly, and the offline suite's stale case used 89,600 against
///     96,768 bytes, so the equal-length case was never once exercised.
///     </para>
///     <para>
///     Step 4 is deliberately a smoke test and not a count. The PowerShell version compared a
///     declared number of config entries and a declared number of distinct sections, which
///     made every settings change a check edit and diagnosed a wrong guid as
///     <c>binary-config-mismatch</c>. With a content hash doing the identity work, the live
///     read is only what it ever honestly was: evidence that the running process loaded
///     SOMETHING for that guid.
///     </para>
/// </remarks>
public static class BinaryAttestation
{
    /// <summary>The launcher's per-instance provisioning record.</summary>
    public const string ProvisionStampName = "provision.stamp";

    /// <summary>SHA-256 of a file, as uppercase hex.</summary>
    public static string HashFile(IFileSystem files, string path) =>
        Convert.ToHexString(SHA256.HashData(files.ReadAllBytes(path)));

    /// <summary>The instance data folder, which is NOT where the game tree lives.</summary>
    /// <remarks>
    ///     Two roots, and both are correct: the launcher puts the game TREE under the
    ///     instances root (typically the game install's volume) and the instance DATA under
    ///     the rig home. The provision stamp and the deployed assembly are both under the
    ///     data folder; the BepInEx log is under the tree.
    /// </remarks>
    public static string InstanceDataFolder(string rigHome, string instance) =>
        Path.Combine(rigHome, "ClientRig", "data", instance);

    /// <summary>Runs the four checks, throwing an inconclusive signal at the first failure.</summary>
    /// <param name="files">The filesystem.</param>
    /// <param name="rigHome">The rig root.</param>
    /// <param name="mod">The derived identity.</param>
    /// <param name="instances">Every instance the check uses.</param>
    /// <param name="readConfigEntryCount">
    ///     Reads the LIVE config entry count for the guid from one instance. This goes through
    ///     the normal reader path so the request lands in the evidence bundle like any other.
    /// </param>
    /// <param name="underTest">
    ///     The mods each instance was provisioned to test, so a missing deploy can be reported
    ///     for the reason it actually has.
    /// </param>
    public static AttestationReport Attest(
        IFileSystem files,
        string rigHome,
        ModIdentity mod,
        IReadOnlyList<string> instances,
        Func<string, int> readConfigEntryCount,
        IReadOnlyList<string>? underTestMods = null)
    {
        var underTest = underTestMods ?? [];
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(readConfigEntryCount);

        if (!files.FileExists(mod.BuildDllPath))
        {
            throw PlaytestSignal.Inconclusive(
                $"The build under test is not on disk at '{mod.BuildDllPath}', so nothing can be said about which build these processes are running. " +
                $"Build it first: dotnet build Mods/{mod.ModName}/{mod.ModName}.sln -c Release",
                Detectors.BinaryMissing);
        }

        var buildBytes = files.GetFileLength(mod.BuildDllPath);
        var buildWrite = Stamps.Format(files.GetLastWriteTimeUtc(mod.BuildDllPath));
        var buildHash = HashFile(files, mod.BuildDllPath);

        var reports = new List<InstanceAttestation>(instances.Count);
        foreach (var instance in instances)
        {
            var dataFolder = InstanceDataFolder(rigHome, instance);
            var stampPath = Path.Combine(dataFolder, ProvisionStampName);

            if (!files.FileExists(stampPath))
            {
                throw PlaytestSignal.Inconclusive(
                    $"'{instance}' has no readable provision stamp at '{stampPath}', so there is no evidence this tree is one the launcher built and a deployed file found in it proves nothing. " +
                    $"Re-provision: testrig create -Target {instance} -Force -As <id>",
                    Detectors.ProvisionStampMissing);
            }

            var stamp = files.ReadAllText(stampPath);

            var deployedPath = Path.Combine(dataFolder, mod.DeployedRelativePath);
            if (!files.FileExists(deployedPath))
            {
                // Its own reason, because the instance's own record changes what "not
                // deployed" MEANS here. An instance provisioned to test this mod does not seed
                // the developer's copy of it, so there is no copy at all rather than a wrong
                // one, and telling a reader to look for a stale file would send them hunting
                // something that cannot be there.
                var underTestHere = underTest.Any(m =>
                    string.Equals(m, mod.ModName, StringComparison.OrdinalIgnoreCase));

                throw PlaytestSignal.Inconclusive(
                    underTestHere
                        ? $"'{instance}' is provisioned to test '{mod.ModName}' and nothing has been deployed: there is no file at '{deployedPath}'. " +
                          $"That instance deliberately does NOT carry the developer's copy either, so it has no '{mod.ModName}' at all and the check would measure its absence. " +
                          $"Deploy this repository's build: testrig deploy {mod.ModName} --target {instance} --as <id>"
                        : $"'{mod.ModName}' is not deployed into '{instance}': nothing at '{deployedPath}'. The check would measure an instance that is not running the mod at all. " +
                          $"Deploy it: testrig deploy {mod.ModName} --target {instance} --as <id>",
                    underTestHere ? Detectors.UnderTestNotDeployed : Detectors.BinaryNotDeployed);
            }

            var deployedHash = HashFile(files, deployedPath);
            if (!string.Equals(deployedHash, buildHash, StringComparison.Ordinal))
            {
                throw PlaytestSignal.Inconclusive(
                    $"'{instance}' is running a DIFFERENT build of {mod.ModName}. " +
                    $"Deployed '{deployedPath}' hashes {deployedHash[..16]}... ({files.GetFileLength(deployedPath)} bytes); " +
                    $"the build under test hashes {buildHash[..16]}... ({buildBytes} bytes). " +
                    "This is a content comparison: two builds of the same length are not the same build, and the PowerShell harness compared length alone. " +
                    $"Deploy it: testrig deploy {mod.ModName} -Target {instance} -As <id>",
                    Detectors.BinaryStale);
            }

            var entries = readConfigEntryCount(instance);
            if (entries <= 0)
            {
                throw PlaytestSignal.Inconclusive(
                    $"'{instance}' reports no configuration at all for guid '{mod.Guid}', so the running process has not loaded the mod even though the right assembly is on disk beside it. " +
                    "The file being correct and the process having loaded it are different facts, and this is the only one of the two that can be read from inside the process.",
                    Detectors.BinaryConfigMismatch);
            }

            reports.Add(new InstanceAttestation(instance, stamp, deployedPath, deployedHash, entries));
        }

        return new AttestationReport(mod, mod.BuildDllPath, buildBytes, buildWrite, buildHash, reports);
    }
}
