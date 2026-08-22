using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace MobiFlight.xplane
{
    /// <summary>
    /// Holds the network location of the X-Plane instance MobiFlight should talk to.
    /// </summary>
    /// <remarks>
    /// Historically MobiFlight assumed that X-Plane runs on the same machine and always used
    /// 127.0.0.1:49000. This type makes the endpoint configurable so that X-Plane can run on a
    /// different machine (and a different operating system, e.g. a Mac or Linux box) than the
    /// Connector.
    /// </remarks>
    public class XplaneConnectionSettings : IEquatable<XplaneConnectionSettings>
    {
        public const string DefaultHost = "127.0.0.1";

        /// <summary>
        /// X-Plane always receives on UDP port 49000, it is not user configurable inside X-Plane.
        /// It is still exposed here because port forwarding or a NAT in between may change it.
        /// </summary>
        public const int DefaultPort = 49000;

        /// <summary>
        /// Host name or IP address of the machine running X-Plane.
        /// </summary>
        public string Host { get; set; } = DefaultHost;

        /// <summary>
        /// UDP port X-Plane listens on.
        /// </summary>
        public int Port { get; set; } = DefaultPort;

        /// <summary>
        /// When enabled, MobiFlight will not require a local X-Plane process to be present before
        /// attempting a connection.
        /// </summary>
        public bool RemoteEnabled { get; set; } = false;

        /// <summary>
        /// True when the configured endpoint points at the local machine.
        /// </summary>
        public bool IsLoopback
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Host)) return true;

                var host = Host.Trim();
                if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;

                return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
            }
        }

        /// <summary>
        /// Builds the settings from the persisted application settings.
        /// </summary>
        public static XplaneConnectionSettings FromApplicationSettings()
        {
            return new XplaneConnectionSettings()
            {
                RemoteEnabled = Properties.Settings.Default.XplaneRemoteEnabled,
                Host = Properties.Settings.Default.XplaneHost,
                Port = Properties.Settings.Default.XplanePort
            };
        }

        /// <summary>
        /// Resolves <see cref="Host"/> to an IPv4 address.
        /// </summary>
        /// <remarks>
        /// The underlying XPlaneConnector library parses the host with IPAddress.Parse and therefore
        /// only accepts literal addresses. Resolving here means users can enter a host name such as
        /// "macbook.local" instead of having to look up the address by hand.
        /// X-Plane's UDP interface is IPv4 only, so IPv6 results are ignored.
        /// </remarks>
        /// <returns>true when the host could be resolved.</returns>
        public bool TryResolveAddress(out IPAddress address, out string error)
        {
            address = null;
            error = null;

            var host = (Host ?? string.Empty).Trim();

            if (string.IsNullOrEmpty(host))
            {
                error = "No X-Plane host configured.";
                return false;
            }

            // A literal IPv4 address needs no lookup at all.
            if (IPAddress.TryParse(host, out var parsed))
            {
                if (parsed.AddressFamily != AddressFamily.InterNetwork)
                {
                    error = $"'{host}' is not an IPv4 address. X-Plane's UDP interface only supports IPv4.";
                    return false;
                }

                address = parsed;
                return true;
            }

            try
            {
                // Note: resolving a ".local" name relies on mDNS being available on this machine.
                address = Dns.GetHostAddresses(host)
                             .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            }
            catch (Exception ex) when (ex is SocketException || ex is ArgumentException)
            {
                error = $"Could not resolve X-Plane host '{host}': {ex.Message}";
                return false;
            }

            if (address == null)
            {
                error = $"Host '{host}' did not resolve to an IPv4 address.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Checks that the settings describe a usable endpoint.
        /// </summary>
        public bool IsValid(out string error)
        {
            if (Port <= 0 || Port > 65535)
            {
                error = $"Port {Port} is outside the valid range 1-65535.";
                return false;
            }

            return TryResolveAddress(out _, out error);
        }

        public XplaneConnectionSettings Clone()
        {
            return new XplaneConnectionSettings()
            {
                Host = Host,
                Port = Port,
                RemoteEnabled = RemoteEnabled
            };
        }

        public bool Equals(XplaneConnectionSettings other)
        {
            if (other is null) return false;

            return string.Equals(Host, other.Host, StringComparison.OrdinalIgnoreCase)
                && Port == other.Port
                && RemoteEnabled == other.RemoteEnabled;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as XplaneConnectionSettings);
        }

        public override int GetHashCode()
        {
            var hash = 17;
            hash = hash * 31 + (Host?.ToLowerInvariant().GetHashCode() ?? 0);
            hash = hash * 31 + Port.GetHashCode();
            hash = hash * 31 + RemoteEnabled.GetHashCode();
            return hash;
        }

        public override string ToString()
        {
            return $"{Host}:{Port}";
        }
    }
}
