using Microsoft.VisualStudio.TestTools.UnitTesting;
using MobiFlight.xplane;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MobiFlightUnitTests.xplane
{
    [TestClass()]
    public class XplaneConnectionTesterTests
    {
        [TestMethod()]
        public void SubscribeDatagramHasExpectedLayoutTest()
        {
            var datagram = XplaneConnectionTester.BuildSubscribeDatagram("sim/test/dataref", frequency: 2, id: 0x1234);

            // X-Plane expects RREF requests to be exactly 413 bytes.
            Assert.HasCount(413, datagram, "RREF datagram has the wrong length");

            Assert.AreEqual("RREF", Encoding.ASCII.GetString(datagram, 0, 4), "Wrong header");
            Assert.AreEqual(0, datagram[4], "Header must be null terminated");

            Assert.AreEqual(2, BitConverter.ToInt32(datagram, 5), "Wrong frequency");
            Assert.AreEqual(0x1234, BitConverter.ToInt32(datagram, 9), "Wrong request id");

            var path = Encoding.ASCII.GetString(datagram, 13, "sim/test/dataref".Length);
            Assert.AreEqual("sim/test/dataref", path, "Wrong dataref path");
            Assert.AreEqual(0, datagram[13 + "sim/test/dataref".Length], "Path must be null padded");
        }

        [TestMethod()]
        public void UnsubscribeUsesZeroFrequencyTest()
        {
            var datagram = XplaneConnectionTester.BuildSubscribeDatagram("sim/test/dataref", frequency: 0, id: 7);

            Assert.AreEqual(0, BitConverter.ToInt32(datagram, 5), "Frequency 0 cancels the subscription");
        }

        [TestMethod()]
        public void OverlongDataRefDoesNotOverflowTest()
        {
            var longPath = new string('a', 1000);

            var datagram = XplaneConnectionTester.BuildSubscribeDatagram(longPath, frequency: 1, id: 1);

            Assert.HasCount(413, datagram, "Datagram must stay at 413 bytes");
            Assert.AreEqual(0, datagram[412], "Last byte must remain the null terminator");
        }

        [TestMethod()]
        public void IsRrefResponseDetectsHeaderTest()
        {
            Assert.IsTrue(XplaneConnectionTester.IsRrefResponse(Encoding.ASCII.GetBytes("RREF\0abcd")));
            Assert.IsFalse(XplaneConnectionTester.IsRrefResponse(Encoding.ASCII.GetBytes("DATA\0abcd")));
            Assert.IsFalse(XplaneConnectionTester.IsRrefResponse(new byte[] { 1, 2 }), "Short buffers are not RREF");
            Assert.IsFalse(XplaneConnectionTester.IsRrefResponse(null), "Null must not throw");
        }

        [TestMethod()]
        public async Task TestAsyncReportsInvalidConfigurationTest()
        {
            var settings = new XplaneConnectionSettings() { Host = "not a host name at all", Port = 49000 };

            var result = await XplaneConnectionTester.TestAsync(settings, TimeSpan.FromSeconds(1));

            Assert.AreEqual(XplaneConnectionTestStatus.InvalidConfiguration, result.Status);
            Assert.IsFalse(result.IsSuccess);
        }

        [TestMethod()]
        public async Task TestAsyncReportsInvalidPortTest()
        {
            var settings = new XplaneConnectionSettings() { Host = "127.0.0.1", Port = 99999 };

            var result = await XplaneConnectionTester.TestAsync(settings, TimeSpan.FromSeconds(1));

            Assert.AreEqual(XplaneConnectionTestStatus.InvalidConfiguration, result.Status);
        }

        [TestMethod()]
        public async Task TestAsyncTimesOutWhenNothingAnswersTest()
        {
            // Bind a socket that never replies so the port is guaranteed to be silent.
            using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)silent.Client.LocalEndPoint).Port;

            var settings = new XplaneConnectionSettings() { Host = "127.0.0.1", Port = port };

            var result = await XplaneConnectionTester.TestAsync(settings, TimeSpan.FromMilliseconds(600));

            Assert.AreEqual(XplaneConnectionTestStatus.NoResponse, result.Status);
            Assert.IsFalse(result.IsSuccess);
            StringAssert.Contains(result.Message, "Accept incoming connections",
                "The message should point the user at the X-Plane network setting");
        }

        [TestMethod()]
        public async Task TestAsyncSucceedsAgainstAFakeSimTest()
        {
            using var fakeSim = new FakeXplane();

            var settings = new XplaneConnectionSettings() { Host = "127.0.0.1", Port = fakeSim.Port };

            var result = await XplaneConnectionTester.TestAsync(settings, TimeSpan.FromSeconds(5));

            Assert.IsTrue(result.IsSuccess, $"Expected success but got: {result.Message}");
            Assert.AreEqual(XplaneConnectionTestStatus.Success, result.Status);
            Assert.IsTrue(fakeSim.ReceivedRrefRequest, "The fake sim did not receive a valid RREF request");
        }

        /// <summary>
        /// Minimal stand-in for X-Plane: listens for an RREF request and answers on the same socket,
        /// which is what the real sim does.
        /// </summary>
        private sealed class FakeXplane : IDisposable
        {
            private readonly UdpClient _socket;
            private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();

            public int Port { get; }
            public bool ReceivedRrefRequest { get; private set; }

            public FakeXplane()
            {
                _socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                Port = ((IPEndPoint)_socket.Client.LocalEndPoint).Port;

                Task.Run(RunAsync);
            }

            private async Task RunAsync()
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    UdpReceiveResult request;
                    try
                    {
                        request = await _socket.ReceiveAsync();
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch (SocketException)
                    {
                        return;
                    }

                    if (request.Buffer.Length != 413) continue;
                    if (Encoding.ASCII.GetString(request.Buffer, 0, 4) != "RREF") continue;

                    ReceivedRrefRequest = true;

                    var id = BitConverter.ToInt32(request.Buffer, 9);

                    // RREF reply: header, null, then pairs of (int id, float value).
                    var reply = new byte[5 + 8];
                    Encoding.ASCII.GetBytes("RREF", 0, 4, reply, 0);
                    BitConverter.GetBytes(id).CopyTo(reply, 5);
                    BitConverter.GetBytes(1234.5f).CopyTo(reply, 9);

                    try
                    {
                        // Reply to the source endpoint, exactly like X-Plane does.
                        await _socket.SendAsync(reply, reply.Length, request.RemoteEndPoint);
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                }
            }

            public void Dispose()
            {
                _cancellation.Cancel();
                _socket.Dispose();
                _cancellation.Dispose();
            }
        }
    }
}
