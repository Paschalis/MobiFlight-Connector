using System.Buffers.Binary;
using System.Text;
using MobiFlight.Core.Xplane;

namespace MobiFlight.Core.Tests.Xplane;

[TestClass]
public sealed class XplaneProtocolTests
{
    [TestMethod]
    public void SubscriptionHasExactLayout()
    {
        var datagram = XplaneProtocol.BuildDataRefSubscription("sim/test/value", frequency: 4, id: 99);

        Assert.HasCount(413, datagram, "X-Plane rejects RREF requests that are not 413 bytes");
        Assert.AreEqual("RREF", Encoding.ASCII.GetString(datagram, 0, 4));
        Assert.AreEqual(0, datagram[4], "header must be null terminated");
        Assert.AreEqual(4, BinaryPrimitives.ReadInt32LittleEndian(datagram.AsSpan(5, 4)));
        Assert.AreEqual(99, BinaryPrimitives.ReadInt32LittleEndian(datagram.AsSpan(9, 4)));
        Assert.AreEqual("sim/test/value", Encoding.ASCII.GetString(datagram, 13, "sim/test/value".Length));
        Assert.AreEqual(0, datagram[13 + "sim/test/value".Length], "path must be null padded");
    }

    [TestMethod]
    public void ZeroFrequencyCancelsSubscription()
    {
        var datagram = XplaneProtocol.BuildDataRefSubscription("sim/test/value", frequency: 0, id: 3);

        Assert.AreEqual(0, BinaryPrimitives.ReadInt32LittleEndian(datagram.AsSpan(5, 4)));
    }

    [TestMethod]
    public void WriteHasExactLayout()
    {
        var datagram = XplaneProtocol.BuildDataRefWrite("sim/test/value", 12.5f);

        Assert.HasCount(509, datagram, "DREF datagrams are 509 bytes");
        Assert.AreEqual("DREF", Encoding.ASCII.GetString(datagram, 0, 4));
        Assert.AreEqual(0, datagram[4]);
        Assert.AreEqual(12.5f, BinaryPrimitives.ReadSingleLittleEndian(datagram.AsSpan(5, 4)));
        Assert.AreEqual("sim/test/value", Encoding.ASCII.GetString(datagram, 9, "sim/test/value".Length));
    }

    [TestMethod]
    public void CommandIsVariableLength()
    {
        var datagram = XplaneProtocol.BuildCommand("sim/systems/avionics_on");

        Assert.HasCount(5 + "sim/systems/avionics_on".Length, datagram);
        Assert.AreEqual("CMND", Encoding.ASCII.GetString(datagram, 0, 4));
        Assert.AreEqual(0, datagram[4]);
        Assert.AreEqual("sim/systems/avionics_on", Encoding.ASCII.GetString(datagram, 5, datagram.Length - 5));
    }

    [TestMethod]
    public void OverlongPathsAreTruncatedNotOverflowed()
    {
        var subscription = XplaneProtocol.BuildDataRefSubscription(new string('x', 5000), 1, 1);
        var write = XplaneProtocol.BuildDataRefWrite(new string('x', 5000), 1f);

        Assert.HasCount(413, subscription);
        Assert.HasCount(509, write);
        Assert.AreEqual(0, subscription[412], "the terminator must survive truncation");
        Assert.AreEqual(0, write[508], "the terminator must survive truncation");
    }

    [TestMethod]
    public void ParsesMultipleValuesFromOnePacket()
    {
        // X-Plane packs as many datarefs into one datagram as fit.
        var packet = new byte[5 + 16];
        Encoding.ASCII.GetBytes("RREF", packet.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(5, 4), 7);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(9, 4), 1.5f);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(13, 4), 8);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(17, 4), 2.5f);

        var values = XplaneProtocol.ParseDataRefResponse(packet);

        Assert.HasCount(2, values);
        Assert.AreEqual(7, values[0].Id);
        Assert.AreEqual(1.5f, values[0].Value);
        Assert.AreEqual(8, values[1].Id);
        Assert.AreEqual(2.5f, values[1].Value);
    }

    [TestMethod]
    public void IgnoresNonDataRefPackets()
    {
        Assert.IsEmpty(XplaneProtocol.ParseDataRefResponse(Encoding.ASCII.GetBytes("DATA\0whatever")));
        Assert.IsEmpty(XplaneProtocol.ParseDataRefResponse([1, 2, 3]));
        Assert.IsFalse(XplaneProtocol.IsDataRefResponse([]));
    }

    [TestMethod]
    public void IgnoresTrailingPartialValue()
    {
        // 5 byte header plus 12 bytes: one full pair and half of a second one.
        var packet = new byte[5 + 12];
        Encoding.ASCII.GetBytes("RREF", packet.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(5, 4), 1);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(9, 4), 3f);

        var values = XplaneProtocol.ParseDataRefResponse(packet);

        Assert.HasCount(1, values, "a truncated trailing pair must be ignored, not misread");
    }
}
