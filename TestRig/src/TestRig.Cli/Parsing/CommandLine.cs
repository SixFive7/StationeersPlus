using System.Globalization;

namespace TestRig.Cli.Parsing;

/// <summary>The caller wrote something the rig cannot act on. Exit code 2.</summary>
/// <remarks>
/// Distinct from a refusal (exit 3), which means the command was well formed and the rig
/// declines to do it. The PowerShell rig collapsed both into "1 unless it carried a
/// sentinel", so a caller could not tell a typo from a rule.
/// </remarks>
public sealed class CliUsageException(string message) : Exception(message);

/// <summary>One parsed command line: the verb, the options, and which of them were typed.</summary>
public sealed class ParsedCommand
{
    private readonly Dictionary<string, string> _values;
    private readonly HashSet<string> _typed;

    internal ParsedCommand(string verb, Dictionary<string, string> values, HashSet<string> typed, IReadOnlyList<string> raw)
    {
        Verb = verb;
        _values = values;
        _typed = typed;
        Raw = raw;
    }

    /// <summary>Lower-cased. Empty when nothing was given, which prints the surface.</summary>
    public string Verb { get; }

    /// <summary>The original argument vector, for echoing a command back at the caller.</summary>
    public IReadOnlyList<string> Raw { get; }

    /// <summary>
    /// Was this option actually written on the command line?
    /// </summary>
    /// <remarks>
    /// The port's replacement for <c>$PSBoundParameters</c>, which is per scope and was
    /// empty inside every function that did not declare its own <c>param</c> block. The
    /// recorded regression: <c>refresh-lock -TtlMinutes 20</c> once tested a function's own
    /// empty dictionary and never applied the new TTL. Three decisions still depend on the
    /// answer: <c>lock</c> forwards <c>--wait-seconds</c> only when typed (its meaning there
    /// is 0, and forwarding the global 300 would turn every lock into a five-minute queue),
    /// <c>refresh-lock</c> forwards both timers only when typed, and refusal 21 fires on the
    /// instance-shape flags that were typed rather than the ones sitting on a default.
    /// </remarks>
    public bool WasTyped(string name) => _typed.Contains(OptionSpec.Normalize(name));

    /// <summary>Every option that was typed, as canonical names, in declaration order.</summary>
    public IReadOnlyList<string> TypedOptions
    {
        get
        {
            var named = new List<string>();
            foreach (var spec in Options.All)
                if (_typed.Contains(spec.Key))
                    named.Add(spec.Name);
            return named;
        }
    }

    public string Text(string name)
    {
        var spec = Options.Get(name);
        return _values.TryGetValue(spec.Key, out var v) ? v : spec.Default;
    }

    public int Number(string name)
    {
        var spec = Options.Get(name);
        var raw = _values.TryGetValue(spec.Key, out var v) ? v : spec.Default;
        return int.Parse(raw, CultureInfo.InvariantCulture);
    }

    /// <summary>The value only when it was typed, otherwise null. For "only if typed" forwarding.</summary>
    public int? NumberIfTyped(string name) => WasTyped(name) ? Number(name) : null;

    public bool Flag(string name)
    {
        var spec = Options.Get(name);
        if (_values.TryGetValue(spec.Key, out var v)) return v == "true";
        return spec.DefaultsTrue;
    }

    /// <summary>The canonical casing of a <see cref="OptionKind.Choice"/> option.</summary>
    public string Choice(string name) => Text(name);
}

/// <summary>
/// The parser. Exact names win, a unique prefix is accepted, anything else is an error
/// that names the candidates.
/// </summary>
/// <remarks>
/// <para>
/// Two positional arguments existed in PowerShell: the verb at 0 and the mod name at 1.
/// The binder did not know which verb was running, so <c>testrig status server</c> bound
/// <c>server</c> to <c>-Mod</c>, left <c>-Target</c> empty, defaulted it to <c>all</c> and
/// reported the whole rig without a word. On the verbs that need an explicit target it
/// failed loudly; on the read-only ones it failed silently, which is worse. Here a second
/// positional argument is accepted by <c>deploy</c> and rejected by everything else, with a
/// message naming <c>--target</c>.
/// </para>
/// <para>
/// Prefix matching is kept because every document and every refusal string written before
/// the port spells options as <c>-Target</c>, and because <c>-Wait 60</c> is in the shipped
/// recipes. PowerShell's rule was: exact match wins, otherwise a unique prefix, otherwise an
/// error. That is reproduced exactly.
/// </para>
/// </remarks>
public static class CommandLine
{
    /// <summary>
    /// Reads one flag out of an argument vector without parsing the rest.
    /// </summary>
    /// <remarks>
    /// The output sink has to be chosen before parsing, because a parse failure must be
    /// reportable in whichever form the caller asked for. Same name resolution as the real
    /// parser, so <c>--json</c>, <c>-Json</c> and a unique prefix all work here too.
    /// </remarks>
    public static bool Peek(IReadOnlyList<string> args, string optionName)
    {
        ArgumentNullException.ThrowIfNull(args);
        var wanted = OptionSpec.Normalize(optionName);
        var result = false;

        foreach (var arg in args)
        {
            if (string.IsNullOrEmpty(arg) || !IsOption(arg)) continue;
            var (name, value) = SplitOption(arg);

            OptionSpec spec;
            bool negated;
            try
            {
                spec = Resolve(name, out negated);
            }
            catch (CliUsageException)
            {
                continue;
            }

            if (spec.Key != wanted) continue;
            if (value is null) result = !negated;
            else
            {
                try
                {
                    result = ParseBool(spec, value) != negated;
                }
                catch (CliUsageException)
                {
                    result = !negated;
                }
            }
        }

        return result;
    }

    public static ParsedCommand Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var typed = new HashSet<string>(StringComparer.Ordinal);
        var verb = string.Empty;
        string? positionalMod = null;
        var sawVerb = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.IsNullOrEmpty(arg)) continue;

            if (!IsOption(arg))
            {
                if (!sawVerb)
                {
                    verb = arg.ToLowerInvariant();
                    sawVerb = true;
                    continue;
                }

                if (positionalMod is not null)
                {
                    throw new CliUsageException(
                        $"'{arg}' is a third bare argument and nothing binds it. Only 'deploy' takes a positional "
                        + "mod name, and only one. Name targets with --target.");
                }

                positionalMod = arg;
                continue;
            }

            var (name, inlineValue) = SplitOption(arg);
            var spec = Resolve(name, out var negated);

            if (spec.Kind == OptionKind.Flag)
            {
                var on = !negated;
                if (inlineValue is not null) on = ParseBool(spec, inlineValue) != negated;
                Set(values, typed, spec, on ? "true" : "false");
                continue;
            }

            if (negated)
                throw new CliUsageException($"--no-{spec.Name} is not a thing: {spec.Display} takes a value.");

            var value = inlineValue;
            if (value is null)
            {
                if (i + 1 >= args.Count)
                    throw new CliUsageException($"{spec.Display} expects a value and none followed it.");
                value = args[++i];
            }

            Set(values, typed, spec, Validate(spec, value));
        }

        if (positionalMod is not null)
        {
            if (!string.Equals(verb, "deploy", StringComparison.Ordinal))
            {
                throw new CliUsageException(
                    $"'{verb}' takes no bare argument, so '{positionalMod}' would have bound to nothing. "
                    + $"Only 'deploy' accepts a positional mod name. Did you mean: testrig {verb} --target {positionalMod}? "
                    + "See TestRig/MANUAL.md.");
            }

            var mod = Options.Get(Options.Mod);
            if (typed.Contains(mod.Key))
                throw new CliUsageException($"'{positionalMod}' and {mod.Display} both name the mods to deploy. Use one.");

            Set(values, typed, mod, positionalMod);
        }

        return new ParsedCommand(verb, values, typed, args);
    }

    private static void Set(Dictionary<string, string> values, HashSet<string> typed, OptionSpec spec, string value)
    {
        values[spec.Key] = value;
        typed.Add(spec.Key);
    }

    /// <summary>An option starts with a dash and is not a negative number.</summary>
    private static bool IsOption(string arg)
    {
        if (arg.Length < 2 || arg[0] != '-') return false;
        return !char.IsDigit(arg[1]);
    }

    private static (string Name, string? Value) SplitOption(string arg)
    {
        var body = arg.TrimStart('-');
        var cut = body.IndexOfAny(['=', ':']);
        if (cut < 0) return (body, null);

        var value = body[(cut + 1)..];
        // PowerShell's -Force:$false spelling survives, because every recipe written before
        // the port uses it and silently binding $false as the string "$false" would be true.
        if (value.StartsWith('$')) value = value[1..];
        return (body[..cut], value);
    }

    private static OptionSpec Resolve(string name, out bool negated)
    {
        negated = false;
        var key = OptionSpec.Normalize(name);
        if (key.Length == 0) throw new CliUsageException("'-' on its own is not an option.");

        if (Options.TryGetExact(key, out var exact)) return exact;

        if (key.StartsWith("no", StringComparison.Ordinal) && key.Length > 2)
        {
            var bare = key[2..];
            if (Options.TryGetExact(bare, out var negatable) && negatable.Kind == OptionKind.Flag)
            {
                negated = true;
                return negatable;
            }
        }

        var candidates = Options.WithPrefix(key);
        if (candidates.Count == 1) return candidates[0];

        if (candidates.Count > 1)
        {
            var names = string.Join(", ", candidates.Select(static c => c.Display));
            throw new CliUsageException($"--{name} is ambiguous: {names}. Write it out.");
        }

        var near = Nearest(key);
        var hint = near.Count > 0 ? $" Did you mean: {string.Join(", ", near)}?" : string.Empty;
        throw new CliUsageException(
            $"--{name} is not a testrig option.{hint} Run 'testrig' with no verb for the whole surface.");
    }

    /// <summary>Options sharing the first three characters, the same rule the verb hint uses.</summary>
    private static IReadOnlyList<string> Nearest(string key)
    {
        var head = key[..Math.Min(3, key.Length)];
        var hits = new List<string>();
        foreach (var spec in Options.All)
            if (spec.Key.StartsWith(head, StringComparison.Ordinal))
                hits.Add(spec.Display);
        return hits;
    }

    private static bool ParseBool(OptionSpec spec, string value) => value.ToLowerInvariant() switch
    {
        "true" or "yes" or "on" or "1" => true,
        "false" or "no" or "off" or "0" => false,
        _ => throw new CliUsageException($"{spec.Display} takes true or false, got '{value}'."),
    };

    private static string Validate(OptionSpec spec, string value)
    {
        switch (spec.Kind)
        {
            case OptionKind.Number:
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    throw new CliUsageException($"{spec.Display} expects a whole number, got '{value}'.");
                return value;

            case OptionKind.Choice:
                foreach (var choice in spec.Choices!)
                    if (string.Equals(choice, value, StringComparison.OrdinalIgnoreCase))
                        return choice;
                throw new CliUsageException(
                    $"{spec.Display} must be one of: {string.Join(", ", spec.Choices!)}. Got '{value}'.");

            default:
                return value;
        }
    }
}
