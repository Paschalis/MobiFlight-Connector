using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MobiFlight.Core.Tests.Xplane;

/// <summary>
/// A stand-in for X-Plane's UDP interface, good enough to exercise a real client end to end.
/// </summary>
/// <remarks>
/// Mirrors the behaviour that matters: it answers on the same socket the request arrived on, which
/// means replies go back to the sender's source port exactly like the real sim.
/// </remarks>
public sealed class FakeXplane : IAsyncDisposable
{
    private readonly UdpClient _socket;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _loop;

    // Subscriptions the client asked for, keyed by dataref path.
    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new(StringComparer.Ordinal);

    /// <summary>Values the fake sim reports, keyed by dataref path.</summary>
    public ConcurrentDictionary<string, float> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>Dataref writes received from the client.</summary>
    public ConcurrentQueue<(string DataRef, float Value)> Writes { get; } = new();

    /// <summary>Commands received from the client.</summary>
    public ConcurrentQueue<string> Commands { get; } = new();

    public int Port { get; }

    public FakeXplane()
    {
        _socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        Port = ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;

        Values[Core.Xplane.XplaneUdpClient.HeartbeatDataRef] = 1f;

        _loop = Task.Run(() => RunAsync(_cancellation.Token));
    }

    public Core.Xplane.XplaneEndpoint Endpoint => new("127.0.0.1", Port);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var publisher = Task.Run(() => PublishAsync(cancellationToken), cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult request;
            try
            {
                request = await _socket.ReceiveAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                break;
            }

            Handle(request);
        }

        await publisher;
    }

    private void Handle(UdpReceiveResult request)
    {
        var buffer = request.Buffer;
        if (buffer.Length < 5) return;

        var header = Encoding.ASCII.GetString(buffer, 0, 4);

        switch (header)
        {
            case "RREF":
            {
                var frequency = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(5, 4));
                var id = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(9, 4));
                var path = ReadPath(buffer, 13);

                if (frequency == 0)
                {
                    _subscriptions.TryRemove(path, out _);
                    return;
                }

                _subscriptions[path] = new Subscription(id, path, request.RemoteEndPoint);
                return;
            }

            case "DREF":
            {
                var value = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(5, 4));
                var path = ReadPath(buffer, 9);
                Values[path] = value;
                Writes.Enqueue((path, value));
                return;
            }

            case "CMND":
            {
                Commands.Enqueue(ReadPath(buffer, 5));
                return;
            }
        }
    }

    /// <summary>
    /// Streams the subscribed values back to whoever asked for them.
    /// </summary>
    private async Task PublishAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var subscription in _subscriptions.Values)
            {
                if (!Values.TryGetValue(subscription.Path, out var value)) continue;

                var packet = new byte[5 + 8];
                Encoding.ASCII.GetBytes("RREF", packet.AsSpan(0, 4));
                BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(5, 4), subscription.Id);
                BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(9, 4), value);

                try
                {
                    await _socket.SendAsync(packet, packet.Length, subscription.ReplyTo);
                }
                catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
                {
                    return;
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static string ReadPath(byte[] buffer, int offset)
    {
        var end = Array.IndexOf(buffer, (byte)0, offset);
        if (end < 0) end = buffer.Length;

        return Encoding.ASCII.GetString(buffer, offset, end - offset);
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        _socket.Dispose();

        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        _cancellation.Dispose();
    }

    private sealed record Subscription(int Id, string Path, IPEndPoint ReplyTo);
}
