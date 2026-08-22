using Microsoft.VisualStudio.TestTools.UnitTesting;
using MobiFlight.xplane;

namespace MobiFlightUnitTests.xplane
{
    [TestClass()]
    public class XplaneConnectionSettingsTests
    {
        [TestMethod()]
        public void DefaultsToLocalXplaneTest()
        {
            var settings = new XplaneConnectionSettings();

            Assert.AreEqual("127.0.0.1", settings.Host, "Default host is not the loopback address");
            Assert.AreEqual(49000, settings.Port, "Default port is not the X-Plane UDP port");
            Assert.IsFalse(settings.RemoteEnabled, "Remote mode must be off by default");
            Assert.IsTrue(settings.IsLoopback, "Default settings must be recognized as local");
        }

        [TestMethod()]
        public void ResolvesLiteralIPv4AddressTest()
        {
            var settings = new XplaneConnectionSettings() { Host = "192.168.1.42" };

            Assert.IsTrue(settings.TryResolveAddress(out var address, out var error), error);
            Assert.AreEqual("192.168.1.42", address.ToString(), "Literal address was not passed through");
        }

        [TestMethod()]
        public void ResolveTrimsWhitespaceTest()
        {
            var settings = new XplaneConnectionSettings() { Host = "  192.168.1.42  " };

            Assert.IsTrue(settings.TryResolveAddress(out var address, out var error), error);
            Assert.AreEqual("192.168.1.42", address.ToString(), "Host was not trimmed before resolving");
        }

        [TestMethod()]
        public void RejectsIPv6AddressTest()
        {
            // X-Plane's UDP interface is IPv4 only.
            var settings = new XplaneConnectionSettings() { Host = "::1" };

            Assert.IsFalse(settings.TryResolveAddress(out _, out var error), "IPv6 must not be accepted");
            Assert.IsNotNull(error, "An explanatory error is expected");
        }

        [TestMethod()]
        public void RejectsEmptyHostTest()
        {
            var settings = new XplaneConnectionSettings() { Host = "   " };

            Assert.IsFalse(settings.TryResolveAddress(out _, out var error), "Empty host must not resolve");
            Assert.IsNotNull(error, "An explanatory error is expected");
        }

        [TestMethod()]
        public void RejectsOutOfRangePortTest()
        {
            var settings = new XplaneConnectionSettings() { Host = "127.0.0.1", Port = 70000 };

            Assert.IsFalse(settings.IsValid(out var error), "Port above 65535 must be rejected");
            Assert.IsNotNull(error, "An explanatory error is expected");

            settings.Port = 0;
            Assert.IsFalse(settings.IsValid(out _), "Port 0 must be rejected");
        }

        [TestMethod()]
        public void LocalhostIsRecognizedAsLoopbackTest()
        {
            Assert.IsTrue(new XplaneConnectionSettings() { Host = "localhost" }.IsLoopback);
            Assert.IsTrue(new XplaneConnectionSettings() { Host = "LOCALHOST" }.IsLoopback);
            Assert.IsTrue(new XplaneConnectionSettings() { Host = "127.0.0.1" }.IsLoopback);
            Assert.IsFalse(new XplaneConnectionSettings() { Host = "192.168.1.42" }.IsLoopback);
        }

        [TestMethod()]
        public void EqualityIgnoresHostCasingTest()
        {
            var a = new XplaneConnectionSettings() { Host = "MacBook.local", Port = 49000, RemoteEnabled = true };
            var b = new XplaneConnectionSettings() { Host = "macbook.local", Port = 49000, RemoteEnabled = true };

            Assert.AreEqual(a, b, "Host comparison must be case insensitive");
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode(), "Equal objects must share a hash code");
        }

        [TestMethod()]
        public void EqualityDetectsChangedEndpointTest()
        {
            var a = new XplaneConnectionSettings() { Host = "192.168.1.42", Port = 49000, RemoteEnabled = true };

            Assert.AreNotEqual(a, new XplaneConnectionSettings() { Host = "192.168.1.43", Port = 49000, RemoteEnabled = true });
            Assert.AreNotEqual(a, new XplaneConnectionSettings() { Host = "192.168.1.42", Port = 49001, RemoteEnabled = true });
            Assert.AreNotEqual(a, new XplaneConnectionSettings() { Host = "192.168.1.42", Port = 49000, RemoteEnabled = false });
        }

        [TestMethod()]
        public void CloneCreatesIndependentCopyTest()
        {
            var original = new XplaneConnectionSettings() { Host = "192.168.1.42", Port = 49001, RemoteEnabled = true };
            var clone = original.Clone();

            Assert.AreNotSame(original, clone, "Clone is the same object");
            Assert.AreEqual(original, clone, "Clone does not equal the original");

            clone.Host = "10.0.0.1";
            Assert.AreEqual("192.168.1.42", original.Host, "Changing the clone changed the original");
        }
    }
}
