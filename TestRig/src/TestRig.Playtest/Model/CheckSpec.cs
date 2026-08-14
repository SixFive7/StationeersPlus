using System.Runtime.CompilerServices;

namespace TestRig.Playtest.Model;

/// <summary>The role the harness brings an instance up as.</summary>
/// <remarks>
///     The declared role only steers bring-up. <b>The live role is <c>/status.role</c></b>,
///     and declaring every instance a client with no host in the list is a used idiom: it
///     leaves bring-up at the menu so the body can drive <c>POST /host</c> itself, which is
///     the only way to reach the window between "reached the menu" and "hosts or connects".
/// </remarks>
public enum InstanceRole
{
    Client,
    Host,
}

/// <summary>
///     One instance a check needs.
/// </summary>
/// <param name="Name">
///     Must already exist in the client rig registry. <b>The harness never creates an
///     instance</b>: creating one costs minutes and rebuilds a tree the caller may not have
///     meant to rebuild.
/// </param>
/// <param name="Role">How bring-up treats it.</param>
/// <param name="World">Host only. A new world id, sent as the host request's world.</param>
/// <param name="Save">Host only. An existing save name.</param>
/// <param name="GamePort">Host only. The RakNet port to host on.</param>
/// <param name="ConnectTo">Client only. Defaults to the FIRST host in the list.</param>
/// <param name="Address">Client only.</param>
public sealed record InstanceSpec(
    string Name,
    InstanceRole Role = InstanceRole.Client,
    string? World = null,
    string? Save = null,
    int? GamePort = null,
    string? ConnectTo = null,
    string Address = "127.0.0.1");

/// <summary>
///     Everything about a check except its body.
/// </summary>
/// <remarks>
///     <para>
///     <b>There is no attestation block.</b> The five keys the PowerShell registration
///     trusted (<c>Mod</c>, <c>ConfigEntryCount</c>, <c>ConfigGroupCount</c>, <c>DllPath</c>,
///     <c>DeployedRelativePath</c>) were declared by the check and validated by nothing, so
///     <c>-Binary @{ Mod = 'net.example' }</c> attested on a parseable provision stamp alone
///     and reported a clean pass (defect P-08). Here the mod, its build and its deployed path
///     are derived from <see cref="SourceFile"/>, which the compiler fills in and a check
///     cannot supply. A check cannot lie about a value it does not provide.
///     </para>
/// </remarks>
public sealed class CheckSpec
{
    /// <summary>
    ///     Declares a check.
    /// </summary>
    /// <param name="name">
    ///     The check's identity: used for selection, the evidence folder slug and the default
    ///     lock purpose. Not required to be unique; the evidence index disambiguates.
    /// </param>
    /// <param name="summary">One line, printed by the listing.</param>
    /// <param name="instances">
    ///     Ordered. Hosts are started in list order before any client, and teardown stops
    ///     non-hosts before hosts in the order they were started, so a check that ends up
    ///     holding a world in a late instance declares the joiner FIRST.
    /// </param>
    /// <param name="purpose">The rig lock purpose. Defaults to "Playtest: &lt;name&gt;".</param>
    /// <param name="ttlMinutes">
    ///     Lock TTL. Deliberately longer than the launcher's own 10, because a check outlives
    ///     ten minutes.
    /// </param>
    /// <param name="sourceFile">
    ///     Supplied by the compiler. Never pass this. It is where the mod's identity, its
    ///     build and its deployed path come from.
    /// </param>
    public CheckSpec(
        string name,
        string summary,
        IReadOnlyList<InstanceSpec> instances,
        string? purpose = null,
        int ttlMinutes = 20,
        [CallerFilePath] string sourceFile = "")
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new PlaytestUsageException("Every check needs a -Name: it is the check's identity in the report, the evidence folder and the lock purpose.");

        ArgumentNullException.ThrowIfNull(instances);
        if (instances.Count == 0)
            throw new PlaytestUsageException($"Check '{name}': every check needs at least one instance. The harness does not create instances, so name one that already exists in the rig registry.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in instances)
        {
            if (string.IsNullOrWhiteSpace(instance.Name))
                throw new PlaytestUsageException($"Check '{name}': every instance entry needs a Name.");

            if (!seen.Add(instance.Name))
                throw new PlaytestUsageException($"Check '{name}': instance '{instance.Name}' is declared twice. Bring-up and teardown both walk this list by name, so a duplicate starts and stops the same process twice.");

            if (instance.Role == InstanceRole.Host)
            {
                // Defect P-01: documented as mutually exclusive and enforced by nothing.
                // Both were sent when both were present, so an instance could be told to
                // create a world AND load a save in one request.
                if (instance.World is not null && instance.Save is not null)
                {
                    throw new PlaytestUsageException(
                        $"Check '{name}': host '{instance.Name}' declares both World and Save. Exactly one: World creates a new world, Save loads an existing one, and sending both asks the host for two different things at once.");
                }

                if (instance.ConnectTo is not null)
                    throw new PlaytestUsageException($"Check '{name}': host '{instance.Name}' declares ConnectTo, which is a client's field. A host is joined, it does not join.");
            }
            else
            {
                if (instance.World is not null || instance.Save is not null)
                {
                    throw new PlaytestUsageException(
                        $"Check '{name}': client '{instance.Name}' declares World or Save, which bring-up would ignore. Declare it a host, or drive the host endpoint from the body, which is what a check does when it needs the window between the menu and hosting.");
                }

                if (instance.GamePort is not null)
                    throw new PlaytestUsageException($"Check '{name}': client '{instance.Name}' declares GamePort, which is the port a HOST listens on.");
            }
        }

        Name = name;
        Summary = summary ?? string.Empty;
        Instances = [.. instances];
        Purpose = string.IsNullOrWhiteSpace(purpose) ? $"Playtest: {name}" : purpose;
        TtlMinutes = ttlMinutes;
        SourceFile = sourceFile;
    }

    public string Name { get; }

    public string Summary { get; }

    public IReadOnlyList<InstanceSpec> Instances { get; }

    public string Purpose { get; }

    public int TtlMinutes { get; }

    /// <summary>The absolute path of the file the check was written in, from the compiler.</summary>
    public string SourceFile { get; }

    /// <summary>The instance names, in declaration order.</summary>
    public IReadOnlyList<string> InstanceNames => [.. Instances.Select(i => i.Name)];

    /// <summary>The names declared as hosts, in declaration order.</summary>
    public IReadOnlyList<string> HostNames => [.. Instances.Where(i => i.Role == InstanceRole.Host).Select(i => i.Name)];
}

/// <summary>A check: what it needs, and what it does.</summary>
public interface IPlaytestCheck
{
    CheckSpec Spec { get; }

    /// <summary>
    ///     The body. Everything around it (the lock, bring-up, attestation, evidence,
    ///     teardown) is the runner's, and a check writes none of it.
    /// </summary>
    void Run(IPlaytestContext context);
}
