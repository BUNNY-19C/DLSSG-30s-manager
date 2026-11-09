namespace DLSSGManager.Cli;

/// <summary>Exit codes, stable for scripts.</summary>
public static class ExitCode
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int Usage = 2;
    public const int NotFound = 3;
    public const int NeedsElevation = 4;
    public const int Blocked = 5;
}

/// <summary><c>--name value</c> options and <c>--flag</c> switches.</summary>
public sealed class Arguments
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "allow-anticheat", "remove-logs" };
    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

    public static Arguments Parse(IEnumerable<string> args)
    {
        var parsed = new Arguments();
        using var e = args.GetEnumerator();
        while (e.MoveNext())
        {
            var token = e.Current;
            if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length < 3)
                throw new ArgumentException("无法识别的参数：" + token);
            var name = token[2..];
            if (parsed._values.ContainsKey(name)) throw new ArgumentException("参数重复：" + token);

            if (Flags.Contains(name))
            {
                parsed._values[name] = null;
                continue;
            }
            if (!e.MoveNext() || e.Current.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("缺少参数值：" + token);
            parsed._values[name] = e.Current;
        }
        return parsed;
    }

    public bool Flag(string name) => _values.ContainsKey(name);

    public string? Optional(string name) => _values.TryGetValue(name, out var v) ? v : null;

    public string Required(string name) =>
        Optional(name) is { Length: > 0 } v ? v : throw new ArgumentException("缺少 --" + name);
}

/// <summary>Reports synchronously on the calling thread (Progress&lt;T&gt; would post to the thread pool and reorder lines).</summary>
internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
