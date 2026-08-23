using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MobiFlight.Core.Web;

namespace MobiFlight.Core.Tests.Web;

[TestClass]
public sealed class FrontendHostTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [TestMethod]
    public void MapsContentTypes()
    {
        Assert.AreEqual("text/html; charset=utf-8", FrontendHost.ContentTypeFor("index.html"));
        Assert.AreEqual("text/javascript; charset=utf-8", FrontendHost.ContentTypeFor("app.js"));
        Assert.AreEqual("text/css; charset=utf-8", FrontendHost.ContentTypeFor("style.css"));
        Assert.AreEqual("image/svg+xml", FrontendHost.ContentTypeFor("logo.svg"));
        Assert.AreEqual("font/woff2", FrontendHost.ContentTypeFor("font.woff2"));
        Assert.AreEqual("application/octet-stream", FrontendHost.ContentTypeFor("unknown.xyz"));
    }

    [TestMethod]
    public async Task ServesStaticFiles()
    {
        using var root = new TempWebRoot();
        root.Write("index.html", "<h1>MobiFlight</h1>");
        root.Write("assets/app.js", "console.log('hi');");

        await using var host = new FrontendHost(FreePort(), root.Path);
        host.Start();

        using var client = new HttpClient { Timeout = Patience };

        var index = await client.GetAsync(host.Url);
        Assert.AreEqual(HttpStatusCode.OK, index.StatusCode);
        Assert.AreEqual("text/html", index.Content.Headers.ContentType?.MediaType);

        var html = await index.Content.ReadAsStringAsync();
        StringAssert.Contains(html, "<h1>MobiFlight</h1>", "the original markup must survive");
        StringAssert.Contains(html, "mobiflight-webview-bridge", "HTML gets the WebView2 shim");

        var script = await client.GetAsync(new Uri(host.Url, "assets/app.js"));
        Assert.AreEqual(HttpStatusCode.OK, script.StatusCode);
        Assert.AreEqual("console.log('hi');", await script.Content.ReadAsStringAsync(),
            "non-HTML assets must be served byte for byte");
    }

    [TestMethod]
    public async Task FallsBackToIndexForClientSideRoutes()
    {
        using var root = new TempWebRoot();
        root.Write("index.html", "<h1>app</h1>");

        await using var host = new FrontendHost(FreePort(), root.Path);
        host.Start();

        using var client = new HttpClient { Timeout = Patience };

        // A React route has no file behind it; the SPA shell must be returned instead of a 404.
        var response = await client.GetAsync(new Uri(host.Url, "settings/devices"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "<h1>app</h1>");
    }

    [TestMethod]
    public async Task RefusesPathTraversal()
    {
        using var root = new TempWebRoot();
        root.Write("index.html", "<h1>app</h1>");

        var secretPath = Path.Combine(Path.GetDirectoryName(root.Path)!, "secret.txt");
        await File.WriteAllTextAsync(secretPath, "top secret");

        try
        {
            await using var host = new FrontendHost(FreePort(), root.Path);
            host.Start();

            using var client = new HttpClient { Timeout = Patience };
            var response = await client.GetAsync(new Uri(host.Url, "../secret.txt"));

            var body = await response.Content.ReadAsStringAsync();
            Assert.AreNotEqual("top secret", body, "a request must not escape the web root");
        }
        finally
        {
            File.Delete(secretPath);
        }
    }

    [TestMethod]
    public async Task ExplainsWhenFrontendIsNotBuilt()
    {
        await using var host = new FrontendHost(FreePort(), webRoot: null);
        host.Start();

        using var client = new HttpClient { Timeout = Patience };
        var response = await client.GetAsync(host.Url);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "npm run build",
            "the response should tell the user how to fix it");
    }

    [TestMethod]
    public async Task BroadcastsMessagesToConnectedBrowser()
    {
        await using var host = new FrontendHost(FreePort(), webRoot: null);
        host.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{host.Url.Authority}/ws"), CancellationToken.None);

        // Wait for the server side to register the client before broadcasting.
        Assert.IsTrue(await Eventually(() => host.ClientCount == 1), "the host never registered the client");

        await host.BroadcastAsync("ConfigValuePartialUpdate", new { dataRef = "sim/test", value = 1.5f });

        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        var json = Encoding.UTF8.GetString(buffer, 0, result.Count);

        using var document = JsonDocument.Parse(json);

        Assert.AreEqual("ConfigValuePartialUpdate", document.RootElement.GetProperty("key").GetString());
        Assert.AreEqual("sim/test", document.RootElement.GetProperty("payload").GetProperty("dataRef").GetString());
    }

    [TestMethod]
    public async Task ReceivesMessagesFromBrowser()
    {
        await using var host = new FrontendHost(FreePort(), webRoot: null);

        var received = new TaskCompletionSource<string>();
        host.MessageReceived += (_, message) => received.TrySetResult(message);

        host.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{host.Url.Authority}/ws"), CancellationToken.None);

        var payload = Encoding.UTF8.GetBytes("""{"key":"CommandMessage","payload":{"action":"start"}}""");
        await socket.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None);

        var completed = await Task.WhenAny(received.Task, Task.Delay(Patience));
        Assert.AreSame(received.Task, completed, "the host never surfaced the browser message");
        StringAssert.Contains(received.Task.Result, "CommandMessage");
    }

    /// <summary>
    /// Overlapping broadcasts must not abort the connection.
    /// </summary>
    /// <remarks>
    /// WebSocket.SendAsync throws if a second send starts while one is in flight, and the socket
    /// is torn down. Broadcasts genuinely do overlap here: initial state on connect, the project
    /// after an edit, and dataref updates from the sim all fire independently.
    /// </remarks>
    [TestMethod]
    public async Task ConcurrentBroadcastsDoNotDropTheClient()
    {
        await using var host = new FrontendHost(FreePort(), webRoot: null);
        host.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{host.Url.Authority}/ws"), CancellationToken.None);

        Assert.IsTrue(await Eventually(() => host.ClientCount == 1));

        // Fire a burst without awaiting between them, which is what the CLI does.
        var payload = new string('x', 4000);
        var sends = Enumerable.Range(0, 40)
            .Select(i => host.BroadcastAsync("Burst", new { Index = i, Filler = payload }))
            .ToArray();

        await Task.WhenAll(sends);

        Assert.AreEqual(1, host.ClientCount, "the client was dropped by overlapping sends");

        // And every message actually arrives, in order.
        for (var expected = 0; expected < 40; expected++)
        {
            var message = await ReceiveOneAsync(socket);
            using var document = JsonDocument.Parse(message);

            Assert.AreEqual(expected, document.RootElement.GetProperty("payload").GetProperty("Index").GetInt32());
        }
    }

    /// <summary>
    /// Reads one whole message, reassembling fragments.
    /// </summary>
    private static async Task<string> ReceiveOneAsync(ClientWebSocket socket)
    {
        var buffer = new byte[8192];
        var builder = new StringBuilder();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

            if (result.EndOfMessage) return builder.ToString();
        }
    }

    /// <summary>
    /// A real project is far bigger than one WebSocket frame, so it must survive fragmentation.
    /// </summary>
    [TestMethod]
    public async Task BroadcastsAFullProjectPayload()
    {
        await using var host = new FrontendHost(FreePort(), webRoot: null);
        var errors = new List<string>();
        host.Log += (_, message) => errors.Add(message);
        host.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{host.Url.Authority}/ws"), CancellationToken.None);
        Assert.IsTrue(await Eventually(() => host.ClientCount == 1));

        // Roughly the size of a real cockpit project.
        var project = Core.Project.MfProject.CreateEmpty("Big");
        for (var i = 0; i < 60; i++)
        {
            var item = project.AddConfigItem($"Item {i}", "OutputConfigItem");
            item["ModuleSerial"] = "Cockpit/ SN-1234-5678";
            item["Source"] = new System.Text.Json.Nodes.JsonObject
            {
                ["SourceType"] = "XPLANE",
                ["XplaneDataRef"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["Path"] = $"sim/cockpit2/some/quite/long/dataref/path/number/{i}",
                },
            };
        }

        await host.BroadcastAsync("Project", project.ToFrontendProject());

        var message = await ReceiveOneAsync(socket);
        using var document = JsonDocument.Parse(message);

        Assert.AreEqual("Project", document.RootElement.GetProperty("key").GetString());
        Assert.AreEqual(60, document.RootElement
            .GetProperty("payload").GetProperty("ConfigFiles")[0].GetProperty("ConfigItems")
            .GetArrayLength());

        Assert.AreEqual(1, host.ClientCount, $"client dropped. Host log: {string.Join("; ", errors)}");
    }

    [TestMethod]
    public async Task BroadcastWithNoClientsIsHarmless()
    {
        await using var host = new FrontendHost(FreePort(), webRoot: null);
        host.Start();

        await host.BroadcastAsync("Anything", new { value = 1 });

        Assert.AreEqual(0, host.ClientCount);
    }

    private static async Task<bool> Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;

        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }

        return false;
    }

    /// <summary>
    /// Asks the OS for an unused TCP port so parallel test runs do not collide.
    /// </summary>
    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class TempWebRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "mobiflight-test-" + Guid.NewGuid().ToString("N"),
            "web");

        public TempWebRoot() => Directory.CreateDirectory(Path);

        public void Write(string relative, string content)
        {
            var full = System.IO.Path.Combine(Path, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                // Best effort cleanup.
            }
        }
    }
}
