using MobiFlight.Core.Xplane;

namespace MobiFlight.Core.Tests.Xplane;

[TestClass]
public sealed class XplaneEndpointTests
{
    [TestMethod]
    public void DefaultsToLocalSim()
    {
        Assert.AreEqual("127.0.0.1", XplaneEndpoint.Local.Host);
        Assert.AreEqual(49000, XplaneEndpoint.Local.Port);
    }

    [TestMethod]
    public void ResolvesLiteralAddress()
    {
        var endpoint = new XplaneEndpoint("192.168.1.10", 49000);

        Assert.IsTrue(endpoint.TryResolve(out var resolved, out var error), error);
        Assert.AreEqual("192.168.1.10:49000", resolved!.ToString());
    }

    [TestMethod]
    public void TrimsWhitespace()
    {
        var endpoint = new XplaneEndpoint("  192.168.1.10  ", 49000);

        Assert.IsTrue(endpoint.TryResolve(out var resolved, out var error), error);
        Assert.AreEqual("192.168.1.10", resolved!.Address.ToString());
    }

    [TestMethod]
    public void RejectsIPv6()
    {
        // X-Plane's UDP interface does not listen on IPv6.
        var endpoint = new XplaneEndpoint("::1", 49000);

        Assert.IsFalse(endpoint.TryResolve(out _, out var error));
        Assert.IsNotNull(error);
    }

    [TestMethod]
    public void RejectsInvalidPorts()
    {
        Assert.IsFalse(new XplaneEndpoint("127.0.0.1", 0).TryResolve(out _, out _));
        Assert.IsFalse(new XplaneEndpoint("127.0.0.1", 70000).TryResolve(out _, out _));
        Assert.IsFalse(new XplaneEndpoint("127.0.0.1", -1).TryResolve(out _, out _));
    }

    [TestMethod]
    public void RejectsEmptyHost()
    {
        Assert.IsFalse(new XplaneEndpoint("   ", 49000).TryResolve(out _, out var error));
        Assert.IsNotNull(error);
    }

    [TestMethod]
    public void ReportsUnresolvableHost()
    {
        var endpoint = new XplaneEndpoint("this-host-does-not-exist.invalid", 49000);

        Assert.IsFalse(endpoint.TryResolve(out _, out var error));
        Assert.IsNotNull(error);
    }

    [TestMethod]
    public void ResolvesLocalhostName()
    {
        Assert.IsTrue(new XplaneEndpoint("localhost", 49000).TryResolve(out var resolved, out var error), error);
        Assert.AreEqual(49000, resolved!.Port);
    }
}
