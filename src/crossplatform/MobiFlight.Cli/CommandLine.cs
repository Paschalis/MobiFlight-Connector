using System.Globalization;
using MobiFlight.Core.Xplane;

namespace MobiFlight.Cli;

/// <summary>
/// Small argument parser. Deliberately dependency free so the CLI stays a single self contained
/// binary with no NuGet packages beyond System.IO.Ports.
/// </summary>
public sealed class CommandLine
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positional { get; } = [];

    public bool WantsHelp => Has("help") || Has("h");

    public static CommandLine Parse(IEnumerable<string> args)
    {
        var result = new CommandLine();
        string? pendingOption = null;

        foreach (var arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal) || (arg.StartsWith('-') && arg.Length == 2))
            {
                // A flag directly followed by another flag is a boolean.
                if (pendingOption is not null) result._options[pendingOption] = null;

                pendingOption = arg.TrimStart('-');

                // Support --key=value as well.
                var separator = pendingOption.IndexOf('=');
                if (separator >= 0)
                {
                    result._options[pendingOption[..separator]] = pendingOption[(separator + 1)..];
                    pendingOption = null;
                }

                continue;
            }

            if (pendingOption is not null)
            {
                result._options[pendingOption] = arg;
                pendingOption = null;
                continue;
            }

            result.Positional.Add(arg);
        }

        if (pendingOption is not null) result._options[pendingOption] = null;

        return result;
    }

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Value(string name) => _options.TryGetValue(name, out var value) ? value : null;

    public int Int(string name, int fallback)
    {
        var raw = Value(name);
        return raw is not null && int.TryParse(raw, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    public double Double(string name, double fallback)
    {
        var raw = Value(name);
        return raw is not null && double.TryParse(raw, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    /// <summary>
    /// The X-Plane endpoint described by --host and --port.
    /// </summary>
    public XplaneEndpoint Endpoint()
    {
        var host = Value("host") ?? "127.0.0.1";

        // "serial probe --port /dev/ttyACM0" reuses --port, so only treat it as a UDP port when
        // it actually parses as a number.
        var port = Int("port", XplaneProtocol.DefaultPort);

        return new XplaneEndpoint(host, port);
    }

    public TimeSpan Timeout() => TimeSpan.FromSeconds(Double("timeout", 5));

    public int Frequency() => Int("freq", 5);
}
