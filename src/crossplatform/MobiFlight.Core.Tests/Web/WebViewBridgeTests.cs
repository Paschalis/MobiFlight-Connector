using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MobiFlight.Core.Web;

namespace MobiFlight.Core.Tests.Web;

/// <summary>
/// The shim is what lets the unmodified frontend build run outside WebView2, so its injection
/// points matter as much as its contents.
/// </summary>
[TestClass]
public sealed class WebViewBridgeTests
{
    [TestMethod]
    public void InjectsAtStartOfHead()
    {
        const string html = "<!doctype html><html><head><title>x</title></head><body></body></html>";

        var result = InjectViaHost(html);

        var bridgeIndex = result.IndexOf("mobiflight-webview-bridge", StringComparison.Ordinal);
        var titleIndex = result.IndexOf("<title>", StringComparison.Ordinal);

        Assert.IsGreaterThan(-1, bridgeIndex, "the shim was not injected");
        Assert.IsLessThan(titleIndex, bridgeIndex,
            "the shim must run before anything else in head, or the app boots without it");
    }

    [TestMethod]
    public void InjectsWhenHeadIsAbsent()
    {
        const string html = "<html><body><div id=\"root\"></div></body></html>";

        var result = InjectViaHost(html);

        StringAssert.Contains(result, "mobiflight-webview-bridge");
        Assert.IsLessThan(result.IndexOf("<body>", StringComparison.Ordinal),
            result.IndexOf("mobiflight-webview-bridge", StringComparison.Ordinal),
            "without a head the shim still has to precede the body");
    }

    [TestMethod]
    public void InjectsIntoBareFragment()
    {
        var result = InjectViaHost("<div id=\"root\"></div>");

        StringAssert.Contains(result, "mobiflight-webview-bridge");
        StringAssert.Contains(result, "<div id=\"root\"></div>");
    }

    [TestMethod]
    public void DoesNotInjectTwice()
    {
        const string html = "<html><head></head><body></body></html>";

        var once = InjectViaHost(html);
        var twice = InjectViaHost(once);

        Assert.AreEqual(once, twice, "re-serving an already patched document must be a no-op");
    }

    [TestMethod]
    public void ProvidesTheTwoApisTheFrontendUses()
    {
        // messageExchange.ts calls postMessage; appMessage.ts calls addEventListener("message").
        StringAssert.Contains(WebViewBridge.Script, "postMessage");
        StringAssert.Contains(WebViewBridge.Script, "addEventListener");
        StringAssert.Contains(WebViewBridge.Script, "window.chrome.webview");
    }

    [TestMethod]
    public void DefersToRealWebView2()
    {
        // Inside the Windows Connector the genuine API must win.
        StringAssert.Contains(WebViewBridge.Script, "if (window.chrome && window.chrome.webview) return;");
    }

    /// <summary>
    /// End to end: a browser speaking the shim's protocol receives a broadcast in the shape the
    /// frontend's message handler expects, with PascalCase payload members.
    /// </summary>
    [TestMethod]
    public async Task BroadcastMatchesFrontendMessageShape()
    {
        await using var host = new FrontendHost(FreePort(), webRoot: null);
        host.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{host.Url.Authority}/ws"), CancellationToken.None);

        var state = new FrontendStateBroadcaster(host);

        // Wait until the server has registered the client.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (host.ClientCount == 0 && DateTime.UtcNow < deadline) await Task.Delay(25);

        await state.SendExecutionStateAsync(running: true);

        var buffer = new byte[8192];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        var json = Encoding.UTF8.GetString(buffer, 0, result.Count);

        using var document = JsonDocument.Parse(json);

        Assert.AreEqual("ExecutionState", document.RootElement.GetProperty("key").GetString());

        var payload = document.RootElement.GetProperty("payload");

        // The TypeScript interface declares PascalCase members. A camelCase policy here would
        // deliver messages the UI silently cannot read.
        Assert.IsTrue(payload.GetProperty("IsRunning").GetBoolean());
        Assert.IsFalse(payload.GetProperty("RunAvailable").GetBoolean());
        Assert.IsFalse(payload.GetProperty("IsTesting").GetBoolean());
    }

    [TestMethod]
    public async Task StatusBarMessageUsesDeclaredCasing()
    {
        await using var host = new FrontendHost(FreePort(), webRoot: null);
        host.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{host.Url.Authority}/ws"), CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (host.ClientCount == 0 && DateTime.UtcNow < deadline) await Task.Delay(25);

        await new FrontendStateBroadcaster(host).SendStatusAsync("Connected", 42);

        var buffer = new byte[8192];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);

        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
        var payload = document.RootElement.GetProperty("payload");

        Assert.AreEqual("Connected", payload.GetProperty("Text").GetString());
        Assert.AreEqual(42, payload.GetProperty("Value").GetInt32());
    }

    /// <summary>
    /// Serves a document through a real host so the injection path under test is the production one.
    /// </summary>
    private static string InjectViaHost(string html)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mobiflight-bridge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(Path.Combine(directory, "index.html"), html);

            var host = new FrontendHost(FreePort(), directory);
            host.Start();

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var served = client.GetStringAsync(host.Url).GetAwaiter().GetResult();

            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return served;
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort cleanup.
            }
        }
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
