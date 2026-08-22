using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace MobiFlight.Core.Xplane;

/// <summary>
/// A portable UDP client for X-Plane.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the XPlaneConnector NuGet package used by the Windows Connector cannot run
/// on macOS or Linux. Its Start() opens one socket, connects it, and then binds a second socket to
/// the same local endpoint. Windows permits that; on macOS and Linux the second bind fails with
/// EADDRINUSE (and SO_REUSEADDR does not help for an already connected unicast socket).
/// </para>
/// <para>
/// The fix is also the simpler design: use a single socket for both directions. X-Plane replies to
/// the source address and port of the request, so the socket that sends the subscription is exactly
/// the socket that receives the values.
/// </para>
/// </remarks>
public sealed class XplaneUdpClient : IAsyncDisposable
{
    /// <summary>
    /// A dataref present in every installation that keeps counting up even while the sim is paused.
    /// Used to tell whether the sim is still talking to us.
    /// </summary>
    public const string HeartbeatDataRef = "sim/time/total_running_time_sec";

    private static readonly TimeSpan ResubscribeInterval = TimeSpan.FromSeconds(5);

    private readonly XplaneEndpoint _endpoint;
    private readonly TimeSpan _connectionTimeout;
    private readonly ConcurrentDictionary<int, Subscription> _byId = new();
    private readonly ConcurrentDictionary<string, Subscription> _byPath = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private UdpClient? _socket;
    private CancellationTokenSource? _cancellation;
    private Task? _receiveLoop;
    private Task? _maintenanceLoop;
    private int _nextId;
    private long _lastReceivedTicks;
    private bool _connected;

    /// <summary>Raised whenever a subscribed dataref reports a new value.</summary>
    public event EventHandler<XplaneDataRefChangedEventArgs>? DataRefChanged;

    /// <summary>Raised the first time data arrives from the sim.</summary>
    public event EventHandler? Connected;

    /// <summary>Raised when the sim stops sending data for longer than the connection timeout.</summary>
    public event EventHandler? Disconnected;

    /// <summary>Raised for diagnostics.</summary>
    public event EventHandler<string>? Log;

    public XplaneUdpClient(XplaneEndpoint endpoint, TimeSpan? connectionTimeout = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _connectionTimeout = connectionTimeout ?? TimeSpan.FromSeconds(15);
    }

    public bool IsConnected => _connected;

    public XplaneEndpoint Endpoint => _endpoint;

    /// <summary>
    /// Opens the socket and starts listening. Does not block waiting for the sim.
    /// </summary>
    public void Start()
    {
        if (_socket is not null) throw new InvalidOperationException("Client already started.");

        if (!_endpoint.TryResolve(out var resolved, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var socket = new UdpClient(AddressFamily.InterNetwork);

        // Connecting binds an ephemeral local port and fixes the destination. X-Plane sends the
        // dataref values straight back to this same address and port.
        socket.Connect(resolved!);

        _socket = socket;
        _cancellation = new CancellationTokenSource();
        _lastReceivedTicks = 0;

        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cancellation.Token));
        _maintenanceLoop = Task.Run(() => MaintenanceLoopAsync(_cancellation.Token));

        Log?.Invoke(this, $"Listening for X-Plane at {resolved} from local port {LocalPort}.");

        // Subscribe to the heartbeat so connection state is known even without user subscriptions.
        _ = SubscribeAsync(HeartbeatDataRef, frequency: 1);
    }

    /// <summary>
    /// The local UDP port in use, or 0 when not started.
    /// </summary>
    public int LocalPort => (_socket?.Client.LocalEndPoint as IPEndPoint)?.Port ?? 0;

    /// <summary>
    /// Subscribes to a dataref. Repeated calls for the same path reuse the existing subscription.
    /// </summary>
    /// <param name="frequency">Updates per second requested from X-Plane.</param>
    public async Task SubscribeAsync(string dataRef, int frequency = 10, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRef);

        if (_byPath.TryGetValue(dataRef, out var existing))
        {
            if (existing.Frequency == frequency) return;

            existing.Frequency = frequency;
        }
        else
        {
            var subscription = new Subscription(Interlocked.Increment(ref _nextId), dataRef, frequency);
            _byPath[dataRef] = subscription;
            _byId[subscription.Id] = subscription;
            existing = subscription;
        }

        await SendAsync(
            XplaneProtocol.BuildDataRefSubscription(existing.Path, existing.Frequency, existing.Id),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels a subscription.
    /// </summary>
    public async Task UnsubscribeAsync(string dataRef, CancellationToken cancellationToken = default)
    {
        if (!_byPath.TryRemove(dataRef, out var subscription)) return;

        _byId.TryRemove(subscription.Id, out _);

        await SendAsync(
            XplaneProtocol.BuildDataRefSubscription(subscription.Path, 0, subscription.Id),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the most recent value received for a dataref, or null when nothing arrived yet.
    /// </summary>
    public float? ReadDataRef(string dataRef)
    {
        return _byPath.TryGetValue(dataRef, out var subscription) && subscription.HasValue
            ? subscription.Value
            : null;
    }

    /// <summary>
    /// Writes a value into a dataref.
    /// </summary>
    public Task WriteDataRefAsync(string dataRef, float value, CancellationToken cancellationToken = default)
    {
        return SendAsync(XplaneProtocol.BuildDataRefWrite(dataRef, value), cancellationToken);
    }

    /// <summary>
    /// Triggers an X-Plane command such as "sim/systems/avionics_on".
    /// </summary>
    public Task SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        return SendAsync(XplaneProtocol.BuildCommand(command), cancellationToken);
    }

    private async Task SendAsync(byte[] datagram, CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null) throw new InvalidOperationException("Client is not started.");

        // UdpClient.SendAsync is not safe for concurrent use on a connected socket.
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(datagram, datagram.Length).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var socket = _socket!;

        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                // On a connected UDP socket an ICMP "port unreachable" surfaces here. The sim may
                // simply not be running yet, so keep listening.
                Log?.Invoke(this, $"Receive error: {ex.SocketErrorCode}");
                continue;
            }

            Interlocked.Exchange(ref _lastReceivedTicks, DateTime.UtcNow.Ticks);
            MarkConnected();

            foreach (var value in XplaneProtocol.ParseDataRefResponse(received.Buffer))
            {
                if (!_byId.TryGetValue(value.Id, out var subscription)) continue;
                if (!subscription.Update(value.Value)) continue;

                DataRefChanged?.Invoke(this, new XplaneDataRefChangedEventArgs(subscription.Path, value.Value));
            }
        }
    }

    /// <summary>
    /// Re-sends subscriptions and watches the heartbeat.
    /// </summary>
    /// <remarks>
    /// X-Plane forgets every subscription when it restarts or when the user loads a new aircraft,
    /// and there is no notification for it. Periodically repeating the request is how third party
    /// clients stay subscribed.
    /// </remarks>
    private async Task MaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ResubscribeInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            foreach (var subscription in _byPath.Values)
            {
                try
                {
                    await SendAsync(
                        XplaneProtocol.BuildDataRefSubscription(subscription.Path, subscription.Frequency, subscription.Id),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log?.Invoke(this, $"Could not refresh subscription for {subscription.Path}: {ex.Message}");
                }
            }

            CheckConnectionState();
        }
    }

    private void MarkConnected()
    {
        if (_connected) return;

        _connected = true;
        Connected?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Declares the connection lost when the sim has gone quiet.
    /// </summary>
    public void CheckConnectionState()
    {
        if (!_connected) return;

        var last = Interlocked.Read(ref _lastReceivedTicks);
        if (last != 0 && DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc) < _connectionTimeout) return;

        _connected = false;

        foreach (var subscription in _byPath.Values) subscription.Reset();

        Log?.Invoke(this, $"No data from X-Plane at {_endpoint} for {_connectionTimeout.TotalSeconds:F0}s.");
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cancellation is null) return;

        await _cancellation.CancelAsync().ConfigureAwait(false);

        _socket?.Dispose();

        foreach (var task in new[] { _receiveLoop, _maintenanceLoop })
        {
            if (task is null) continue;

            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        _cancellation.Dispose();
        _cancellation = null;
        _socket = null;
        _sendLock.Dispose();
    }

    private sealed class Subscription(int id, string path, int frequency)
    {
        public int Id { get; } = id;
        public string Path { get; } = path;
        public int Frequency { get; set; } = frequency;
        public float Value { get; private set; }
        public bool HasValue { get; private set; }

        /// <summary>Stores a new value and reports whether it actually changed.</summary>
        public bool Update(float value)
        {
            if (HasValue && Value.Equals(value)) return false;

            Value = value;
            HasValue = true;
            return true;
        }

        public void Reset() => HasValue = false;
    }
}

public sealed class XplaneDataRefChangedEventArgs(string dataRef, float value) : EventArgs
{
    public string DataRef { get; } = dataRef;
    public float Value { get; } = value;
}
