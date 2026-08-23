using System.Runtime.InteropServices;
using MobiFlight.Core.Devices;

namespace MobiFlight.Core.Tests.Devices;

[TestClass]
public sealed class CmdMessengerChannelTests
{
    [TestMethod]
    public void BuildsPlainCommand()
    {
        // SetPin(13, 1) on the wire.
        Assert.AreEqual("2,13,1;", CmdMessengerChannel.Build(2, 13, 1));
    }

    [TestMethod]
    public void BuildsCommandWithoutArguments()
    {
        Assert.AreEqual("9;", CmdMessengerChannel.Build(9));
    }

    [TestMethod]
    public void EscapesSeparatorsInArguments()
    {
        // A display value containing a comma must not look like a field break.
        Assert.AreEqual(@"1,0,Left\, Right;", CmdMessengerChannel.Build(1, 0, "Left, Right"));
        Assert.AreEqual(@"1,0,semi\;colon;", CmdMessengerChannel.Build(1, 0, "semi;colon"));
        Assert.AreEqual(@"1,0,back\\slash;", CmdMessengerChannel.Build(1, 0, @"back\slash"));
    }

    [TestMethod]
    public void UsesInvariantNumberFormat()
    {
        // A comma decimal separator would corrupt the framing on some locales.
        Assert.AreEqual("4,1.5;", CmdMessengerChannel.Build(4, 1.5f));
    }

    [TestMethod]
    public void ParsesMessageFromStream()
    {
        var channel = NewOfflineChannel();
        var received = new List<CmdMessage>();
        channel.MessageReceived += (_, m) => received.Add(m);

        channel.Consume("10,MobiFlight Mega,My Mega,SN-123,2.5.1;");

        Assert.HasCount(1, received);
        Assert.AreEqual(10, received[0].CommandId);
        Assert.AreEqual("MobiFlight Mega", received[0].Argument(0));
        Assert.AreEqual("My Mega", received[0].Argument(1));
        Assert.AreEqual("SN-123", received[0].Argument(2));
        Assert.AreEqual("2.5.1", received[0].Argument(3));
    }

    [TestMethod]
    public void ParsesMessageSplitAcrossReads()
    {
        // Serial ports deliver arbitrary chunks, not whole messages.
        var channel = NewOfflineChannel();
        var received = new List<CmdMessage>();
        channel.MessageReceived += (_, m) => received.Add(m);

        channel.Consume("10,Mobi");
        channel.Consume("Flight Mega,My Me");
        Assert.IsEmpty(received, "no message should surface before the terminator");

        channel.Consume("ga,SN-1,2.5.1;");

        Assert.HasCount(1, received);
        Assert.AreEqual("MobiFlight Mega", received[0].Argument(0));
    }

    [TestMethod]
    public void ParsesSeveralMessagesInOneChunk()
    {
        var channel = NewOfflineChannel();
        var received = new List<CmdMessage>();
        channel.MessageReceived += (_, m) => received.Add(m);

        channel.Consume("7,Button1,0;6,Encoder1,2;");

        Assert.HasCount(2, received);
        Assert.AreEqual(7, received[0].CommandId);
        Assert.AreEqual(6, received[1].CommandId);
        Assert.AreEqual(2, received[1].ArgumentAsInt(1));
    }

    [TestMethod]
    public void EscapedSeparatorStaysInsideItsField()
    {
        // This is the case that a "split after unescaping" implementation gets wrong.
        var channel = NewOfflineChannel();
        var received = new List<CmdMessage>();
        channel.MessageReceived += (_, m) => received.Add(m);

        channel.Consume(@"10,Type,Left\, Panel,SN-1,1.0.0;");

        Assert.HasCount(1, received);
        Assert.HasCount(4, received[0].Arguments, "the command id is not one of the arguments");
        Assert.AreEqual("Left, Panel", received[0].Argument(1));
        Assert.AreEqual("SN-1", received[0].Argument(2), "the escaped comma must not have split the field");
    }

    [TestMethod]
    public void EscapedTerminatorDoesNotEndMessage()
    {
        var channel = NewOfflineChannel();
        var received = new List<CmdMessage>();
        channel.MessageReceived += (_, m) => received.Add(m);

        channel.Consume(@"10,a\;b;");

        Assert.HasCount(1, received);
        Assert.AreEqual("a;b", received[0].Argument(0));
    }

    [TestMethod]
    public void RoundTripsThroughBuildAndConsume()
    {
        var channel = NewOfflineChannel();
        var received = new List<CmdMessage>();
        channel.MessageReceived += (_, m) => received.Add(m);

        channel.Consume(CmdMessengerChannel.Build(1, 0, @"tricky,;\value"));

        Assert.HasCount(1, received);
        Assert.AreEqual(@"tricky,;\value", received[0].Argument(1));
    }

    [TestMethod]
    public void IgnoresNoiseAndEmptyMessages()
    {
        var channel = NewOfflineChannel();
        var received = new List<CmdMessage>();
        channel.MessageReceived += (_, m) => received.Add(m);

        channel.Consume(";;\r\n;not-a-number,x;");

        Assert.IsEmpty(received, "unparseable frames must be dropped, not throw");
    }

    /// <summary>
    /// A channel bound to a port name that is never opened, so only the parser is exercised.
    /// </summary>
    private static CmdMessengerChannel NewOfflineChannel()
    {
        return new CmdMessengerChannel("/dev/null-not-opened", 115200);
    }
}

[TestClass]
public sealed class SerialPortScannerTests
{
    [TestMethod]
    public void RejectsEmptyPortNames()
    {
        Assert.IsFalse(SerialPortScanner.IsLikelyBoard(""));
        Assert.IsFalse(SerialPortScanner.IsLikelyBoard("   "));
        Assert.IsFalse(SerialPortScanner.IsLikelyBoard(null!));
    }

    [TestMethod]
    public void RecognisesPlatformBoardNames()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Assert.IsTrue(SerialPortScanner.IsLikelyBoard("/dev/ttyACM0"), "Leonardo/Micro style board");
            Assert.IsTrue(SerialPortScanner.IsLikelyBoard("/dev/ttyUSB0"), "FTDI/CH340 adapter");
            Assert.IsFalse(SerialPortScanner.IsLikelyBoard("/dev/ttyS0"), "built-in UART is not a board");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Assert.IsTrue(SerialPortScanner.IsLikelyBoard("/dev/cu.usbmodem14201"));
            Assert.IsTrue(SerialPortScanner.IsLikelyBoard("/dev/cu.wchusbserial1420"));
            Assert.IsFalse(SerialPortScanner.IsLikelyBoard("/dev/cu.Bluetooth-Incoming-Port"));
        }
        else
        {
            Assert.IsTrue(SerialPortScanner.IsLikelyBoard("COM3"));
            Assert.IsFalse(SerialPortScanner.IsLikelyBoard("/dev/ttyACM0"));
        }
    }

    [TestMethod]
    public void EnumerationDoesNotThrow()
    {
        // The CI runners have no boards attached; the call must still succeed and return a list.
        var ports = SerialPortScanner.GetPortNames(includeUnlikely: true);

        Assert.IsNotNull(ports);
    }

    [TestMethod]
    public void TroubleshootingHintIsPlatformSpecific()
    {
        var hint = SerialPortScanner.GetTroubleshootingHint();

        Assert.IsFalse(string.IsNullOrWhiteSpace(hint));

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            StringAssert.Contains(hint, "dialout", "Linux users need the dialout group");
        }
    }
}
