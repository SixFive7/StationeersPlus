using System.Diagnostics.CodeAnalysis;
using TestRig.Contracts;
using TestRig.Core.Abstractions;
using TestRig.Playtest.Values;

namespace TestRig.Playtest.Model;

/// <summary>
///     Everything a check body can do.
/// </summary>
/// <remarks>
///     <para>
///     The verbs split in two and do not mix. <see cref="Act"/> MAKES something happen and
///     returns an <see cref="ActionResult"/>, which no assert verb accepts.
///     <see cref="Read"/> READS a named value from a named instance through a named reader
///     and returns an <see cref="Observation"/>, the only thing the assert verbs take.
///     </para>
///     <para>
///     There is deliberately no <c>AssertOk</c>, no <c>AssertTrue</c> and no bare-boolean
///     assert. An endpoint answering ok is a statement about the request, not about the
///     world. Which instance is the authority depends on the question: anything the server
///     owns (the roster, whether hosting happened, a simulated object's state) is
///     authoritative on the HOST; anything a client half decides for itself is authoritative
///     on THAT client; "did the joiner arrive" is the host's roster, never the joiner's own
///     answer.
///     </para>
/// </remarks>
public interface IPlaytestContext
{
    /// <summary>The check being run.</summary>
    CheckSpec Check { get; }

    /// <summary>The rig session lock owner id for this check.</summary>
    string Owner { get; }

    /// <summary>The rig home, for a check that needs to reach an instance's own data folder.</summary>
    string RigHome { get; }

    /// <summary>
    ///     The filesystem, for a check that has to stage a fixture into an instance's own
    ///     tier-3 save root.
    /// </summary>
    /// <remarks>
    ///     A check that reaches for <c>System.IO</c> directly is unfakeable and therefore
    ///     untestable, which is how the one check that seeds a mod into an instance came to
    ///     have no coverage of the cleanup that is the dangerous half of it.
    /// </remarks>
    IFileSystem Files { get; }

    /// <summary>
    ///     Notes the report carries out of teardown. A check may append its own; check 05
    ///     does, to shout about a fixture it could not remove.
    /// </summary>
    IList<string> TeardownNotes { get; }

    /// <summary>How many assertions this check has actually made.</summary>
    /// <remarks>
    ///     Defect P-02: there was no counter anywhere in the PowerShell library, so a check
    ///     with a valid binary block and an empty body reported a clean pass, and the offline
    ///     suite registered exactly that shape twice while asserting only the result count.
    ///     A pass is now gated on this being non-zero, exactly as it is gated on attestation.
    /// </remarks>
    int AssertionCount { get; }

    // ---- driving ----------------------------------------------------------

    /// <summary>
    ///     Make something happen.
    /// </summary>
    /// <param name="on">The instance to drive. Must be one of this check's own.</param>
    /// <param name="path">An <see cref="Endpoints"/> constant.</param>
    /// <param name="body">A Contracts request record, or null for a bodyless call.</param>
    /// <param name="blocking">
    ///     Marks an endpoint that freezes that instance's whole control plane, so a transport
    ///     silence is explained rather than treated as a dead instance. The host, connect,
    ///     save, load, new-world and wait-for endpoints are treated as blocking automatically.
    /// </param>
    /// <param name="noRetry">One attempt only. Used in cleanup, where a retry storm is noise.</param>
    /// <param name="timeoutSeconds">Overrides the default (330 s blocking, 120 s otherwise).</param>
    ActionResult Act(string on, string path, object? body = null, bool blocking = false, bool noRetry = false, int? timeoutSeconds = null);

    /// <summary>Read one named value from one named instance through one named reader.</summary>
    /// <param name="from">The instance that is the AUTHORITY for this value.</param>
    /// <param name="reader">Which reader.</param>
    /// <param name="select">A dotted path with array indexing and a <c>count</c> pseudo-member.</param>
    /// <param name="of">Reader-specific narrowing: a clientId, a referenceId, a Section/Key.</param>
    /// <param name="readerArgs">A Contracts request record; becomes the query string.</param>
    Observation Read(string from, Reader reader, string select = ".", string of = "", object? readerArgs = null);

    // ---- asserting: the only things that can produce a fail ----------------

    /// <summary>Read a value from the authority and require it.</summary>
    /// <param name="because">
    ///     Mandatory. A report saying "hosting was False" is a puzzle; one saying why it
    ///     matters is a finding.
    /// </param>
    Observation AssertValue(string from, Reader reader, ValueMatcher matcher, string because, string select = ".", string of = "", object? readerArgs = null);

    /// <summary>Require two or more instances to agree, and optionally on a particular value.</summary>
    IReadOnlyList<Observation> AssertAgreement(IReadOnlyList<string> across, Reader reader, string because, string select = ".", string of = "", object? readerArgs = null, object? isValue = null, bool pinValue = false);

    /// <summary>Re-read a baseline's exact request and require the value to have moved, or not.</summary>
    Observation AssertChange(Observation baseline, string because, object? to = null, bool unchanged = false);

    /// <summary>
    ///     Assert that the build under test is the one these processes are running.
    /// </summary>
    /// <remarks>
    ///     Runs automatically before the body. A check that somehow never attests is
    ///     downgraded from pass to inconclusive.
    /// </remarks>
    void AssertBinaryUnderTest();

    /// <summary>Declare that the check cannot make its observation. Always throws.</summary>
    /// <remarks>
    ///     There is deliberately no failure counterpart. Marked <see cref="DoesNotReturnAttribute"/>
    ///     so the compiler knows a guard that declines has ended the check, which is what lets a
    ///     check read a nullable value straight after guarding it.
    /// </remarks>
    [DoesNotReturn]
    void SetInconclusive(string because, string detector = Detectors.CheckDeclined, IReadOnlyDictionary<string, object?>? detail = null);

    // ---- rig operations ---------------------------------------------------

    /// <summary>Wait for an instance to reach a readiness stage. Returns the status at that moment.</summary>
    StatusResponse WaitStage(string name, Stage stage, int waitSeconds = 300, int pollSeconds = 5);

    /// <summary>Stop and start ONE instance by name. Never rig-wide.</summary>
    void RestartInstance(string name, string reason = "");

    /// <summary>
    ///     Connect a joiner to a host and confirm it from the HOST's roster.
    /// </summary>
    /// <remarks>
    ///     Use this rather than driving the connect endpoint directly. It reads the port off
    ///     the host, POLLS the roster (inWorld on the joiner and the row appearing server-side
    ///     are different instants) and retries from the menu. Four checks once reported
    ///     joiner-not-in-roster on a rig that was joining fine, purely because each carried
    ///     its own copy of that logic.
    /// </remarks>
    JoinResult ConnectJoiner(string name, string to, string address = "127.0.0.1", int port = 0, int attempts = 3, double gapSeconds = 10, int rosterPollSeconds = 30);

    /// <summary>Append a console tail for every instance, labelled with a step. Never throws.</summary>
    void SaveConsoleTail(string step, IReadOnlyList<string>? instances = null);

    /// <summary>Write a file into the evidence bundle. Returns null when there is no bundle.</summary>
    string? WriteEvidence(string name, string content, EvidenceKind kind = EvidenceKind.Root, bool append = false);

    /// <summary>Sleep, through the injected sleeper, so the offline suite pays nothing for it.</summary>
    void Wait(double seconds);

    /// <summary>The current UTC stamp in the one format the bundle uses.</summary>
    string Stamp();
}
