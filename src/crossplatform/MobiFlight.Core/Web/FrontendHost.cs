using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MobiFlight.Core.Web;

/// <summary>
/// The envelope MobiFlight uses to talk to its frontend.
/// </summary>
/// <remarks>
/// Matches the shape produced by the Windows Connector's PostMessagePublisher, so the same React
/// frontend can be driven from here: the key is the message type name and the payload is the
/// message itself.
/// </remarks>
public sealed record MessageEnvelope(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("payload")] object Payload);

/// <summary>
/// Serves the MobiFlight React frontend over HTTP and talks to it over a WebSocket.
/// </summary>
/// <remarks>
/// <para>
/// The Windows Connector hosts the frontend inside a WebView2 control, which is Windows only.
/// Here the same static build is served over plain HTTP so it can be opened in any browser on
/// macOS or Linux, with a WebSocket carrying the messages that WebView2 would carry via postMessage.
/// </para>
/// <para>
/// Deliberately built on HttpListener rather than ASP.NET Core to keep the portable build free of
/// extra package dependencies.
/// </para>
/// </remarks>
public sealed class FrontendHost : IAsyncDisposable
{
    /// <summary>
    /// Property names are serialized exactly as declared.
    /// </summary>
    /// <remarks>
    /// The frontend's TypeScript interfaces use PascalCase members ("Text", "IsRunning"), matching
    /// what Newtonsoft produces for the Connector's message classes. Applying a camelCase policy
    /// here would silently deliver messages the UI cannot read. Only the envelope's own "key" and
    /// "payload" are lowercase, and those are spelled out with attributes.
    /// </remarks>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpListener _listener = new();
    private readonly string? _webRoot;
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();
    private readonly CancellationTokenSource _cancellation = new();

    private Task? _acceptLoop;

    public FrontendHost(int port = 8080, string? webRoot = null, string host = "localhost")
    {
        Url = new Uri($"http://{host}:{port}/");
        _listener.Prefixes.Add(Url.ToString());
        _webRoot = webRoot;
    }

    public Uri Url { get; }

    /// <summary>Directory the static frontend is served from, if one was found.</summary>
    public string? WebRoot => _webRoot;

    /// <summary>Number of connected browsers.</summary>
    public int ClientCount => _clients.Count;

    /// <summary>Raised for every message a browser sends.</summary>
    public event EventHandler<string>? MessageReceived;

    /// <summary>
    /// Raised once a browser's WebSocket is ready. Use it to send the initial state, since the
    /// frontend has no way to ask for it.
    /// </summary>
    public event EventHandler? ClientConnected;

    /// <summary>Raised for diagnostics.</summary>
    public event EventHandler<string>? Log;

    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cancellation.Token));
    }

    /// <summary>
    /// Sends a message to every connected browser, using the type name as the key.
    /// </summary>
    public Task BroadcastAsync<T>(T payload) where T : notnull
    {
        return BroadcastAsync(typeof(T).Name, payload);
    }

    /// <summary>
    /// Sends a message to every connected browser under an explicit key.
    /// </summary>
    public async Task BroadcastAsync(string key, object payload)
    {
        if (_clients.IsEmpty) return;

        var json = JsonSerializer.Serialize(new MessageEnvelope(key, payload), JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        foreach (var (id, socket) in _clients)
        {
            if (socket.State != WebSocketState.Open)
            {
                _clients.TryRemove(id, out _);
                continue;
            }

            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, _cancellation.Token)
                            .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                _clients.TryRemove(id, out _);
            }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            // Handle each request independently so a slow one cannot stall the listener.
            _ = Task.Run(() => HandleAsync(context, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (context.Request.IsWebSocketRequest)
            {
                await AcceptWebSocketAsync(context, cancellationToken).ConfigureAwait(false);
                return;
            }

            ServeStaticFile(context);
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
        {
            // The browser went away mid-response; nothing to do.
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"Request failed: {ex.Message}");
        }
    }

    private async Task AcceptWebSocketAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var wsContext = await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
        var id = Guid.NewGuid();
        _clients[id] = wsContext.WebSocket;

        Log?.Invoke(this, $"Browser connected ({_clients.Count} total).");
        ClientConnected?.Invoke(this, EventArgs.Empty);

        var buffer = new byte[8192];
        var socket = wsContext.WebSocket;

        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cancellationToken)
                                .ConfigureAwait(false);
                    break;
                }

                MessageReceived?.Invoke(this, Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // Normal disconnect.
        }
        finally
        {
            _clients.TryRemove(id, out _);
            socket.Dispose();
            Log?.Invoke(this, $"Browser disconnected ({_clients.Count} left).");
        }
    }

    private void ServeStaticFile(HttpListenerContext context)
    {
        if (_webRoot is null || !Directory.Exists(_webRoot))
        {
            WriteText(context, 503,
                "The MobiFlight frontend has not been built.\n\n" +
                "Build it with:\n" +
                "  cd src/MobiFlightConnector/frontend && npm install && npm run build\n\n" +
                "then start again with --web-root pointing at frontend/dist.");
            return;
        }

        var relative = Uri.UnescapeDataString(context.Request.Url?.AbsolutePath ?? "/").TrimStart('/');
        if (relative.Length == 0) relative = "index.html";

        var path = Path.GetFullPath(Path.Combine(_webRoot, relative));

        // Refuse anything that escapes the web root.
        if (!path.StartsWith(Path.GetFullPath(_webRoot), StringComparison.Ordinal))
        {
            WriteText(context, 403, "Forbidden");
            return;
        }

        // Single page app: unknown paths fall back to index.html so client side routing works.
        if (!File.Exists(path))
        {
            path = Path.Combine(_webRoot, "index.html");

            if (!File.Exists(path))
            {
                WriteText(context, 404, "Not found");
                return;
            }
        }

        byte[] bytes;

        if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            // HTML gets the WebView2 shim so the unmodified frontend build works in a browser.
            bytes = Encoding.UTF8.GetBytes(WebViewBridge.Inject(File.ReadAllText(path)));
        }
        else
        {
            bytes = File.ReadAllBytes(path);
        }

        context.Response.StatusCode = 200;
        context.Response.ContentType = ContentTypeFor(path);
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes);
        context.Response.OutputStream.Close();
    }

    private static void WriteText(HttpListenerContext context, int status, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);

        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes);
        context.Response.OutputStream.Close();
    }

    internal static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".ico" => "image/x-icon",
        ".webp" => "image/webp",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".map" => "application/json; charset=utf-8",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// Looks for a built frontend near the executable or in the source tree.
    /// </summary>
    public static string? FindWebRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(directory.FullName, "frontend"),
                         Path.Combine(directory.FullName, "wwwroot"),
                         Path.Combine(directory.FullName, "src", "MobiFlightConnector", "frontend", "dist"),
                     })
            {
                if (File.Exists(Path.Combine(candidate, "index.html"))) return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync().ConfigureAwait(false);

        foreach (var (_, socket) in _clients)
        {
            try
            {
                socket.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Already gone.
            }
        }

        _clients.Clear();

        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
            // Already stopped.
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        _cancellation.Dispose();
    }
}
