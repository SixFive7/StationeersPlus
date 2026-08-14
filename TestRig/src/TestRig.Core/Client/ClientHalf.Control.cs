using System.Text;
using System.Text.Json;
using TestRig.Contracts;
using TestRig.Core.Session;

namespace TestRig.Core.Client;

public sealed partial class ClientHalf
{
    // =====================================================================
    // call
    // =====================================================================

    /// <summary>
    /// One HTTP request to each selected instance's control plane.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The old <c>call</c> and <c>broadcast</c> in one operation, because they differed only
    /// in how many targets they had and in the fan-out's own shorter hardcoded timeout.
    /// Naming one target prints the parsed answer; naming several prints one line and one
    /// compact object each, and either shape throws if anything failed, because a partial
    /// fan-out leaves the rig in mixed state (CLIENT-265).
    /// </para>
    /// <para>
    /// Gated because it drives LIVE clients: <c>/quit</c> ends one, and <c>/savepath</c>
    /// retargets where one writes its saves. That endpoint refuses the developer's real
    /// user-data folder ONLY while the caller omits <c>force=true</c>, and that refusal is
    /// plugin-side policy rather than a rule an agent reads first, so it is recorded here at
    /// the call site (CLIENT-257, CLIENT-258). <b>Never pass force=true unless the user asked
    /// for exactly that.</b>
    /// </para>
    /// <para>
    /// BOTH branches now agree about what failure means (CLIENT-261, CLIENT-263, CLIENT-264
    /// fixed). The PowerShell's single-target branch had no try/catch at all, so every
    /// hosting refusal documented in the manual surfaced as "Response status code does not
    /// indicate success: 409 (Conflict)" and the diagnostic body was discarded, through the
    /// verb an agent actually types. Its fan-out branch caught the throw but printed the
    /// exception message rather than the plugin's explanation.
    /// </para>
    /// </remarks>
    public async Task CallAsync(
        IReadOnlyList<InstanceEntry> entries,
        string path,
        string? body = null,
        string? callerId = null,
        int callTimeoutSeconds = 0,
        CancellationToken ct = default)
    {
        AssertGate("call", callerId);

        if (entries.Count == 0)
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                "'call' needs at least one instance. Name one with --target <name>, or fan out with "
                + "--target clients.");
        }

        // Derived from the request rather than pinned, so a body asking for timeoutMs 300000
        // actually gets its five minutes.
        var timeout = _control.TimeoutSecondsFor(path, body, callTimeoutSeconds);

        // Asked before anything is sent: a 404 costs a lock, a launch and a round trip to
        // discover, and the answer is already known here.
        if (!Endpoints.Exists(path))
        {
            var suggestions = Endpoints.Suggest(path);
            var hint = suggestions.Count > 0
                ? $" Did you mean one of: {string.Join(", ", suggestions)}?"
                : $" GET {Endpoints.Help} on a running instance lists every path.";
            Warn($"[Call] '{path}' is not a path the plugin answers.{hint}");
        }

        var failed = 0;

        if (entries.Count == 1)
        {
            var entry = entries[0];
            Say($"[Call] {entry.InstanceName} {path} (up to {timeout}s)");

            var answer = await _control.RawAsync(entry.Port, path, body, timeout, ct).ConfigureAwait(false);
            var ok = OutcomeOf(answer);

            if (ok)
            {
                Say(JsonText.Pretty(answer.Body));
                _output.Value("response", answer.Body);
            }
            else
            {
                failed++;
                Warn($"[{entry.InstanceName}] {ControlPlane.ErrorDetail(answer)}");
                if (!string.IsNullOrWhiteSpace(answer.Body)) Say(JsonText.Pretty(answer.Body));
                _output.Value("error", ControlPlane.ErrorDetail(answer));
            }
        }
        else
        {
            Say($"[Call] {path} -> {entries.Count} instance(s), up to {timeout}s each");

            foreach (var entry in entries)
            {
                var answer = await _control.RawAsync(entry.Port, path, body, timeout, ct).ConfigureAwait(false);
                var ok = OutcomeOf(answer);

                if (!ok)
                {
                    failed++;
                    Warn($"[{entry.InstanceName}] {ControlPlane.ErrorDetail(answer)}");
                }

                Say($"[{entry.InstanceName}] ok={ok.ToString().ToLowerInvariant()}");
                if (!string.IsNullOrWhiteSpace(answer.Body)) Say(JsonText.Compact(answer.Body));
            }
        }

        _output.Value("callFailed", failed);

        if (failed > 0)
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                $"[Call] {failed} of {entries.Count} instance(s) failed. A partial fan-out leaves the rig in "
                + "mixed state; fix and re-run before drawing any conclusion from a test.");
        }
    }

    /// <summary>
    /// Whether one raw answer counts as success.
    /// </summary>
    /// <remarks>
    /// A missing <c>ok</c> field is treated as success, which is what the fan-out did and
    /// what a handful of endpoints legitimately produce. Neither the status alone nor
    /// <c>ok</c> alone is sufficient: a config lookup failure arrives as
    /// <c>{"ok":false}</c> at HTTP 200, and a refusal arrives with the identical body at 409.
    /// </remarks>
    private static bool OutcomeOf(ControlAnswer answer)
    {
        if (!answer.Answered) return false;
        if (string.IsNullOrWhiteSpace(answer.Body)) return answer.HttpStatus == RigStatus.Ok;

        try
        {
            using var doc = JsonDocument.Parse(answer.Body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("ok", out var ok)
                && ok.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return ok.GetBoolean();
            }
        }
        catch (JsonException)
        {
            // A non-JSON body at 200 is the screenshot endpoint's inline PNG, which is a
            // success. At any other status it is a transport oddity, which is not.
            return answer.HttpStatus == RigStatus.Ok;
        }

        return answer.HttpStatus == RigStatus.Ok;
    }

    // =====================================================================
    // snapshot
    // =====================================================================

    /// <summary>
    /// Fetches <c>/status</c> from each selected instance and writes them out.
    /// </summary>
    /// <remarks>
    /// Not lock-gated: it only reads (CLIENT-277). Always a JSON ARRAY, even for one
    /// instance (CLIENT-274), because the playtest harness's before-and-after diffing depends
    /// on the shape being stable.
    /// </remarks>
    public async Task<string> SnapshotAsync(
        IReadOnlyList<InstanceEntry> entries,
        string? outFile = null,
        CancellationToken ct = default)
    {
        var rows = new List<SnapshotRow>(entries.Count);

        foreach (var entry in entries)
        {
            var (status, error) = await _control.StatusAsync(entry.Port, 15, ct).ConfigureAwait(false);
            rows.Add(new SnapshotRow
            {
                InstanceName = entry.InstanceName,
                Port = entry.Port,
                Status = status,
                Error = status is null ? error : null,
            });
        }

        var json = JsonSerializer.Serialize(rows.ToArray(), ClientJsonContext.Default.SnapshotRowArray);

        if (string.IsNullOrEmpty(outFile))
        {
            Say(json);
            return json;
        }

        var target = ResolveOutFile(outFile);
        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir)) _fs.CreateDirectory(dir);
        _fs.WriteAllText(target, json);

        Say($"[Snapshot] {rows.Count} instance(s) -> {target}");
        _output.Value("snapshotPath", target);
        _output.Value("snapshotCount", rows.Count);
        return json;
    }

    /// <summary>
    /// Where a snapshot actually lands, with the rig folder as the floor for anything relative.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A relative path used to resolve against the shell's working directory, which for an
    /// agent is the repository root. Rooting it at the rig folder fixed the common case,
    /// because that folder is gitignored deny-all and a stray snapshot there cannot be
    /// committed by accident (CLIENT-269).
    /// </para>
    /// <para>
    /// Two forms still walked out and are REFUSED rather than written: <c>..\..\x.json</c>
    /// is relative, gets joined, and then climbs straight back out (CLIENT-270); and
    /// <c>C:x.json</c> is "rooted" by the naive test but is DRIVE-RELATIVE, resolved against
    /// a per-drive working directory, so nothing here can say where it would land
    /// (CLIENT-268). Fully qualified means the path names its own root, which is not the
    /// same as rooted (CLIENT-266).
    /// </para>
    /// <para>
    /// A fully qualified path outside the rig is HONOURED with a warning (CLIENT-272): the
    /// rule is that an explicit full path is the caller taking responsibility. Porting all
    /// three as one uniform refusal would lose a working case.
    /// </para>
    /// </remarks>
    public string ResolveOutFile(string value)
    {
        var qualified = Path.IsPathFullyQualified(value);

        if (!qualified && value.Contains(':', StringComparison.Ordinal))
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                $"[Snapshot] --out-file '{value}' is drive-relative: Windows resolves it against a per-drive "
                + $"working directory, so nothing here can say where it would land. Pass a path relative to the "
                + $"rig folder ({_layout.ClientRoot}), or a full path including the leading backslash.");
        }

        var target = qualified ? value : Path.Combine(_layout.ClientRoot, value);
        var full = Path.GetFullPath(target);
        var rigBase = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_layout.ClientRoot))
                      + Path.DirectorySeparatorChar;
        var inside = full.StartsWith(rigBase, StringComparison.OrdinalIgnoreCase);

        if (!qualified && !inside)
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                $"[Snapshot] --out-file '{value}' climbs out of the rig folder (it resolves to {full}). A "
                + $"relative --out-file is rooted at {_layout.ClientRoot}, which is gitignored deny-all, "
                + "precisely so a stray snapshot cannot be committed by accident. Drop the '..' segments, or "
                + "pass a full path if you really mean to write outside the rig.");
        }

        if (!inside)
        {
            Warn($"[Snapshot] {full} is outside the rig folder, so the deny-all gitignore does not cover it. "
                 + "Make sure it is not somewhere that gets committed.");
        }

        return full;
    }
}

/// <summary>
/// Rendering arbitrary JSON without knowing its shape.
/// </summary>
/// <remarks>
/// <see cref="JsonElement.WriteTo(Utf8JsonWriter)"/> rather than a serializer call, because
/// an AOT binary has no reflection-based fallback and there is no generated type info for a
/// response whose shape is only known at runtime. A body that is not JSON is returned
/// unchanged: the screenshot endpoint answers with raw PNG bytes at 200, and a transport
/// error page is not JSON either.
/// </remarks>
public static class JsonText
{
    public static string Pretty(string? body) => Render(body, indented: true);

    public static string Compact(string? body) => Render(body, indented: false);

    private static string Render(string? body, bool indented)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";

        try
        {
            using var doc = JsonDocument.Parse(body);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = indented }))
            {
                doc.RootElement.WriteTo(writer);
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException)
        {
            return body;
        }
    }
}
