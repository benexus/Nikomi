namespace Nikomi.Core;

/// <summary>
/// Reads a command line the way both programs take it: one command word at most, switches such as
/// "--background", and options followed by their value ("--data-dir FOLDER"). The last of a repeated
/// option counts. What each program then does with an argument it does not know is its own choice:
/// the console program refuses it, the app logs it and starts anyway (it has no console to say so).
/// </summary>
public sealed class Arguments
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _switches = new(StringComparer.Ordinal);
    private readonly List<string> _unknown = new();

    private Arguments() { }

    /// <summary>The command word (the first argument that does not start with "-"), or null.</summary>
    public string? Command { get; private set; }

    /// <summary>Arguments that are neither a known switch, a known option nor the one command word.</summary>
    public IReadOnlyList<string> Unknown => _unknown;

    /// <param name="switches">Arguments that stand alone.</param>
    /// <param name="options">Arguments that take the next argument as their value (null when there is none).</param>
    /// <param name="command">Whether a command word is taken; without one, a bare word is unknown.</param>
    public static Arguments Parse(IReadOnlyList<string> args, IEnumerable<string> switches, IEnumerable<string> options, bool command)
    {
        var known = new HashSet<string>(switches, StringComparer.Ordinal);
        var valued = new HashSet<string>(options, StringComparer.Ordinal);
        var parsed = new Arguments();
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (valued.Contains(a)) parsed._values[a] = i + 1 < args.Count ? args[++i] : null;
            else if (known.Contains(a)) parsed._switches.Add(a);
            else if (command && parsed.Command == null && !a.StartsWith('-')) parsed.Command = a;
            else parsed._unknown.Add(a);
        }
        return parsed;
    }

    public bool Has(string name) => _switches.Contains(name);

    /// <summary>The option's value, or null when it was not given or had no value after it.</summary>
    public string? Value(string name) => _values.GetValueOrDefault(name);

    /// <summary>The option's value as a whole number, or null when it was not given or is not one.</summary>
    public int? Number(string name) => int.TryParse(Value(name), out int n) ? n : null;
}
