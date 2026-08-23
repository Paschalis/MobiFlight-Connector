using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MobiFlight.Core.Project;
using MobiFlight.Core.Web;

namespace MobiFlight.Core.Tests.Web;

/// <summary>
/// Mirrors what the CLI's "serve" command does when a browser attaches: a burst of state messages
/// followed by the project, all fired from the ClientConnected event.
/// </summary>
/// <remarks>
/// This exists because the burst is where several independent broadcasts overlap, which is exactly
/// the situation a single WebSocket cannot tolerate.
/// </remarks>
[TestClass]
public sealed class ServeStartupTests
{
    [TestMethod]
    public async Task ClientSurvivesTheFullInitialBurst()
    {
        await using var host = new FrontendHost(FreePort(), webRoot: null);

        var log = new List<string>();
        host.Log += (_, message) => { lock (log) log.Add(message); };

        var state = new FrontendStateBroadcaster(host);
        var commands = new FrontendCommandHandler(host, state);
        commands.Log += (_, message) => { lock (log) log.Add(message); };

        var project = MfProject.CreateEmpty("Cockpit");
        for (var i = 0; i < 30; i++) project.AddConfigItem($"Item {i}", "OutputConfigItem");
        commands.Project = project;

        var controllers = new List<FrontendMessages.Controller>
        {
            new("Mega", "SN-1", "MobiFlight Mega", "/dev/ttyACM0"),
        };

        // Exactly the CLI's handler: fire and forget, both calls, no awaiting between them.
        host.ClientConnected += (_, _) => _ = Task.Run(async () =>
        {
            await state.SendInitialStateAsync("Waiting for X-Plane", controllers, running: false);
            await commands.PublishProjectAsync();
        });

        host.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{host.Url.Authority}/ws"), CancellationToken.None);

        var keys = new List<string>();
        var deadline = DateTime.UtcNow.AddSeconds(15);

        while (DateTime.UtcNow < deadline && !keys.Contains("Project"))
        {
            string message;
            try
            {
                message = await ReceiveOneAsync(socket);
            }
            catch (WebSocketException ex)
            {
                Assert.Fail($"Connection dropped during the burst: {ex.Message}. Host log: {Dump(log)}");
                return;
            }

            using var document = JsonDocument.Parse(message);
            keys.Add(document.RootElement.GetProperty("key").GetString()!);
        }

        CollectionAssert.Contains(keys, "OverlayState", $"log: {Dump(log)}");
        CollectionAssert.Contains(keys, "ExecutionState");
        CollectionAssert.Contains(keys, "ConnectedControllers");
        CollectionAssert.Contains(keys, "StatusBarUpdate");
        CollectionAssert.Contains(keys, "Project", "the project never arrived");

        Assert.AreEqual(WebSocketState.Open, socket.State, "the socket should still be usable");
    }

    private static string Dump(List<string> log)
    {
        lock (log) return log.Count == 0 ? "(empty)" : string.Join(" | ", log);
    }

    private static async Task<string> ReceiveOneAsync(ClientWebSocket socket)
    {
        var buffer = new byte[8192];
        var builder = new StringBuilder();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException($"server sent close: {result.CloseStatusDescription}");
            }

            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage) return builder.ToString();
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
