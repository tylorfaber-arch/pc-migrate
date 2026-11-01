namespace PcMigrate.Cli;

/// <summary>Tiny parser for "command --flag value --switch" style arguments.</summary>
internal sealed class Args
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

    public Args(string[] argv)
    {
        Command = argv.Length > 0 && !argv[0].StartsWith('-') ? argv[0].ToLowerInvariant() : "help";
        for (var i = Command == "help" && (argv.Length == 0 || argv[0].StartsWith('-')) ? 0 : 1; i < argv.Length; i++)
        {
            var a = argv[i];
            if (!a.StartsWith("--")) throw new ArgumentException($"Unexpected argument '{a}'.");
            var name = a[2..];
            string value = "true";
            var eq = name.IndexOf('=');
            if (eq > 0) { value = name[(eq + 1)..]; name = name[..eq]; }
            else if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--")) value = argv[++i];
            if (!_values.TryGetValue(name, out var list)) _values[name] = list = [];
            list.Add(value);
        }
    }

    public string Command { get; }

    public string? Get(string name)
    {
        _used.Add(name);
        return _values.TryGetValue(name, out var v) ? v[^1] : null;
    }

    public string Require(string name) => Get(name) ?? throw new ArgumentException($"--{name} is required.");

    public IReadOnlyList<string> GetAll(string name)
    {
        _used.Add(name);
        return _values.TryGetValue(name, out var v) ? v : [];
    }

    public bool Flag(string name) => Get(name) is { } v && !v.Equals("false", StringComparison.OrdinalIgnoreCase);

    public int GetInt(string name, int fallback) =>
        Get(name) is { } v ? (int.TryParse(v, out var n) ? n : throw new ArgumentException($"--{name} must be a number.")) : fallback;

    /// <summary>Call after reading every option, so typos like --usr are reported instead of ignored.</summary>
    public void RejectUnknown()
    {
        var unknown = _values.Keys.Where(k => !_used.Contains(k)).ToList();
        if (unknown.Count > 0) throw new ArgumentException($"Unknown option(s): {string.Join(", ", unknown.Select(u => "--" + u))}");
    }
}
