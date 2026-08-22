using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MobiFlight.xplane
{
    public enum XplaneConnectionTestStatus
    {
        Success,
        InvalidConfiguration,
        HostUnreachable,
        NoResponse
    }

    public class XplaneConnectionTestResult
    {
        public XplaneConnectionTestStatus Status { get; set; }
        public string Message { get; set; }
        public TimeSpan RoundTrip { get; set; }

        public bool IsSuccess => Status == XplaneConnectionTestStatus.Success;
    }

    /// <summary>
    /// Performs a one-shot check that an X-Plane instance is reachable at a given endpoint.
    /// </summary>
    /// <remarks>
    /// This deliberately does not use the XPlaneConnector library. It uses a single UDP socket for
    /// both sending and receiving, which is what X-Plane expects: it replies to the source address
    /// and port of the request. A single socket is also the only portable option, because binding a
    /// second socket to an address already in use fails on macOS and Linux.
    /// </remarks>
    public static class XplaneConnectionTester
    {
        /// <summary>
        /// A dataref that exists in every X-Plane installation regardless of the loaded aircraft.
        /// </summary>
        private const string ProbeDataRef = "sim/time/total_running_time_sec";

        private const int ProbeId = 0x4D46; // "MF"
        private const int RrefDatagramLength = 413;

        public static async Task<XplaneConnectionTestResult> TestAsync(
            XplaneConnectionSettings settings,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            if (settings.Port <= 0 || settings.Port > 65535)
            {
                return new XplaneConnectionTestResult
                {
                    Status = XplaneConnectionTestStatus.InvalidConfiguration,
                    Message = $"Port {settings.Port} is outside the valid range 1-65535."
                };
            }

            if (!settings.TryResolveAddress(out var address, out var resolveError))
            {
                return new XplaneConnectionTestResult
                {
                    Status = XplaneConnectionTestStatus.InvalidConfiguration,
                    Message = resolveError
                };
            }

            var endpoint = new IPEndPoint(address, settings.Port);
            var started = DateTime.UtcNow;

            using (var socket = new UdpClient(AddressFamily.InterNetwork))
            using (var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                try
                {
                    // Connecting binds the socket to an ephemeral local port. X-Plane sends the
                    // subscription data back to exactly that address and port.
                    socket.Connect(endpoint);

                    var request = BuildSubscribeDatagram(ProbeDataRef, frequency: 2, id: ProbeId);
                    await socket.SendAsync(request, request.Length).ConfigureAwait(false);

                    timeoutSource.CancelAfter(timeout);

                    while (!timeoutSource.IsCancellationRequested)
                    {
                        var receiveTask = socket.ReceiveAsync();
                        var completed = await Task.WhenAny(
                            receiveTask,
                            Task.Delay(Timeout.Infinite, timeoutSource.Token)).ConfigureAwait(false);

                        if (completed != receiveTask) break;

                        var response = await receiveTask.ConfigureAwait(false);

                        if (IsRrefResponse(response.Buffer))
                        {
                            // Be a good citizen and cancel the subscription again.
                            var unsubscribe = BuildSubscribeDatagram(ProbeDataRef, frequency: 0, id: ProbeId);
                            try
                            {
                                await socket.SendAsync(unsubscribe, unsubscribe.Length).ConfigureAwait(false);
                            }
                            catch (SocketException)
                            {
                                // Not being able to unsubscribe does not invalidate the test result.
                            }

                            return new XplaneConnectionTestResult
                            {
                                Status = XplaneConnectionTestStatus.Success,
                                RoundTrip = DateTime.UtcNow - started,
                                Message = $"X-Plane answered from {endpoint}."
                            };
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    // Timed out, handled by falling through to the NoResponse result.
                }
                catch (SocketException ex)
                {
                    return new XplaneConnectionTestResult
                    {
                        Status = XplaneConnectionTestStatus.HostUnreachable,
                        Message = $"Could not reach {endpoint}: {ex.Message}"
                    };
                }
                catch (ObjectDisposedException)
                {
                    // Socket torn down while waiting, treat as no response.
                }
            }

            return new XplaneConnectionTestResult
            {
                Status = XplaneConnectionTestStatus.NoResponse,
                Message =
                    $"No answer from {endpoint}. Check that X-Plane is running, that " +
                    "\"Accept incoming connections\" is enabled under Settings > Network, and that " +
                    "UDP port " + settings.Port + " is allowed through the firewall on the machine running X-Plane."
            };
        }

        /// <summary>
        /// Builds an RREF datagram. A frequency of 0 cancels an existing subscription.
        /// </summary>
        internal static byte[] BuildSubscribeDatagram(string dataRef, int frequency, int id)
        {
            var datagram = new byte[RrefDatagramLength];
            var position = 0;

            // Header, null terminated.
            Encoding.ASCII.GetBytes("RREF", 0, 4, datagram, 0);
            position += 5;

            WriteLittleEndian(datagram, position, frequency);
            position += 4;

            WriteLittleEndian(datagram, position, id);
            position += 4;

            // The remainder holds the null padded dataref path.
            var path = Encoding.ASCII.GetBytes(dataRef);
            var length = Math.Min(path.Length, RrefDatagramLength - position - 1);
            Buffer.BlockCopy(path, 0, datagram, position, length);

            return datagram;
        }

        internal static bool IsRrefResponse(byte[] buffer)
        {
            if (buffer == null || buffer.Length < 5) return false;

            return buffer[0] == (byte)'R'
                && buffer[1] == (byte)'R'
                && buffer[2] == (byte)'E'
                && buffer[3] == (byte)'F';
        }

        private static void WriteLittleEndian(byte[] target, int offset, int value)
        {
            target[offset + 0] = (byte)(value & 0xFF);
            target[offset + 1] = (byte)((value >> 8) & 0xFF);
            target[offset + 2] = (byte)((value >> 16) & 0xFF);
            target[offset + 3] = (byte)((value >> 24) & 0xFF);
        }
    }
}
