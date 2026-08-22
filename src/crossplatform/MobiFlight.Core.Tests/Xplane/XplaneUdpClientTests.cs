using MobiFlight.Core.Xplane;

namespace MobiFlight.Core.Tests.Xplane;

[TestClass]
public sealed class XplaneUdpClientTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task ConnectsAndReportsHeartbeat()
    {
        await using var sim = new FakeXplane();
        await using var client = new XplaneUdpClient(sim.Endpoint);

        var connected = new TaskCompletionSource();
        client.Connected += (_, _) => connected.TrySetResult();

        client.Start();

        Assert.IsTrue(await Completes(connected.Task), "client never reported a connection");
        Assert.IsTrue(client.IsConnected);
        Assert.AreNotEqual(0, client.LocalPort, "a local port should be bound");
    }

    [TestMethod]
    public async Task ReceivesSubscribedDataRefValues()
    {
        await using var sim = new FakeXplane();
        sim.Values["sim/cockpit/radios/com1_freq_hz"] = 12180f;

        await using var client = new XplaneUdpClient(sim.Endpoint);

        var received = new TaskCompletionSource<float>();
        client.DataRefChanged += (_, e) =>
        {
            if (e.DataRef == "sim/cockpit/radios/com1_freq_hz") received.TrySetResult(e.Value);
        };

        client.Start();
        await client.SubscribeAsync("sim/cockpit/radios/com1_freq_hz", frequency: 10);

        Assert.IsTrue(await Completes(received.Task), "no value arrived for the subscribed dataref");
        Assert.AreEqual(12180f, received.Task.Result);
        Assert.AreEqual(12180f, client.ReadDataRef("sim/cockpit/radios/com1_freq_hz"));
    }

    [TestMethod]
    public async Task RaisesChangeOnlyWhenValueActuallyChanges()
    {
        await using var sim = new FakeXplane();
        sim.Values["sim/test/steady"] = 42f;

        await using var client = new XplaneUdpClient(sim.Endpoint);

        var changes = 0;
        client.DataRefChanged += (_, e) =>
        {
            if (e.DataRef == "sim/test/steady") Interlocked.Increment(ref changes);
        };

        client.Start();
        await client.SubscribeAsync("sim/test/steady", frequency: 20);

        // The fake sim republishes every 50 ms; a constant value must still only fire once.
        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.AreEqual(1, changes, "an unchanged value should not raise repeated change events");
    }

    [TestMethod]
    public async Task WritesDataRefToSim()
    {
        await using var sim = new FakeXplane();
        await using var client = new XplaneUdpClient(sim.Endpoint);

        client.Start();
        await client.WriteDataRefAsync("sim/cockpit/electrical/battery_on", 1f);

        Assert.IsTrue(await Eventually(() => sim.Writes.Count > 0), "the sim never saw the write");

        Assert.IsTrue(sim.Writes.TryDequeue(out var write));
        Assert.AreEqual("sim/cockpit/electrical/battery_on", write.DataRef);
        Assert.AreEqual(1f, write.Value);
    }

    [TestMethod]
    public async Task SendsCommandToSim()
    {
        await using var sim = new FakeXplane();
        await using var client = new XplaneUdpClient(sim.Endpoint);

        client.Start();
        await client.SendCommandAsync("sim/systems/avionics_on");

        Assert.IsTrue(await Eventually(() => sim.Commands.Count > 0), "the sim never saw the command");

        Assert.IsTrue(sim.Commands.TryDequeue(out var command));
        Assert.AreEqual("sim/systems/avionics_on", command);
    }

    [TestMethod]
    public async Task UnsubscribeStopsUpdates()
    {
        await using var sim = new FakeXplane();
        sim.Values["sim/test/counter"] = 1f;

        await using var client = new XplaneUdpClient(sim.Endpoint);

        var received = new TaskCompletionSource();
        client.DataRefChanged += (_, e) =>
        {
            if (e.DataRef == "sim/test/counter") received.TrySetResult();
        };

        client.Start();
        await client.SubscribeAsync("sim/test/counter", frequency: 20);
        Assert.IsTrue(await Completes(received.Task));

        await client.UnsubscribeAsync("sim/test/counter");

        // After unsubscribing the client forgets the dataref entirely.
        Assert.IsNull(client.ReadDataRef("sim/test/counter"));
    }

    [TestMethod]
    public async Task ReportsDisconnectWhenSimGoesQuiet()
    {
        var sim = new FakeXplane();
        await using var client = new XplaneUdpClient(sim.Endpoint, connectionTimeout: TimeSpan.FromMilliseconds(300));

        var connected = new TaskCompletionSource();
        var disconnected = new TaskCompletionSource();
        client.Connected += (_, _) => connected.TrySetResult();
        client.Disconnected += (_, _) => disconnected.TrySetResult();

        client.Start();
        Assert.IsTrue(await Completes(connected.Task), "never connected in the first place");

        // Pull the sim away, as happens when the machine sleeps or X-Plane is closed.
        await sim.DisposeAsync();

        await Task.Delay(TimeSpan.FromMilliseconds(600));
        client.CheckConnectionState();

        Assert.IsTrue(await Completes(disconnected.Task), "the watchdog did not notice the silent sim");
        Assert.IsFalse(client.IsConnected);
    }

    [TestMethod]
    public async Task StartFailsForUnresolvableHost()
    {
        await using var client = new XplaneUdpClient(new XplaneEndpoint("this-host-does-not-exist.invalid", 49000));

        Assert.ThrowsExactly<InvalidOperationException>(client.Start);
    }

    [TestMethod]
    public async Task StartFailsForInvalidPort()
    {
        await using var client = new XplaneUdpClient(new XplaneEndpoint("127.0.0.1", 99999));

        Assert.ThrowsExactly<InvalidOperationException>(client.Start);
    }

    private static async Task<bool> Completes(Task task)
    {
        return await Task.WhenAny(task, Task.Delay(Patience)) == task;
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
}
