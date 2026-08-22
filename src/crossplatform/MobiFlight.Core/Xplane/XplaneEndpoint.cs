using System.Net;
using System.Net.Sockets;

namespace MobiFlight.Core.Xplane;

/// <summary>
/// The network location of an X-Plane instance.
/// </summary>
public sealed record XplaneEndpoint(string Host, int Port)
{
    public static XplaneEndpoint Local { get; } = new("127.0.0.1", XplaneProtocol.DefaultPort);

    /// <summary>
    /// Resolves the host to an IPv4 endpoint.
    /// </summary>
    /// <remarks>
    /// X-Plane's UDP interface is IPv4 only, so IPv6 results are filtered out. Host names are
    /// supported so a Mac can be reached as "macbook.local" where mDNS is available.
    /// </remarks>
    public bool TryResolve(out IPEndPoint? endpoint, out string? error)
    {
        endpoint = null;
        error = null;

        if (Port is <= 0 or > 65535)
        {
            error = $"Port {Port} is outside the valid range 1-65535.";
            return false;
        }

        var host = (Host ?? string.Empty).Trim();

        if (host.Length == 0)
        {
            error = "No X-Plane host configured.";
            return false;
        }

        if (IPAddress.TryParse(host, out var literal))
        {
            if (literal.AddressFamily != AddressFamily.InterNetwork)
            {
                error = $"'{host}' is not an IPv4 address. X-Plane's UDP interface only supports IPv4.";
                return false;
            }

            endpoint = new IPEndPoint(literal, Port);
            return true;
        }

        IPAddress? resolved;
        try
        {
            resolved = Array.Find(
                Dns.GetHostAddresses(host),
                a => a.AddressFamily == AddressFamily.InterNetwork);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            error = $"Could not resolve X-Plane host '{host}': {ex.Message}";
            return false;
        }

        if (resolved is null)
        {
            error = $"Host '{host}' did not resolve to an IPv4 address.";
            return false;
        }

        endpoint = new IPEndPoint(resolved, Port);
        return true;
    }

    public override string ToString() => $"{Host}:{Port}";
}
