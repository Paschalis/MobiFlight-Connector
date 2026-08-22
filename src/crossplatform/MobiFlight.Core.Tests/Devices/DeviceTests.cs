using System.Runtime.InteropServices;
using MobiFlight.Core.Devices;

namespace MobiFlight.Core.Tests.Devices;

[TestClass]
public sealed class MobiFlightBoardProbeTests
{
    [TestMethod]
    public void SplitsPlainFields()
    {
        var fields = MobiFlightBoardProbe.SplitFields("10,MobiFlight Mega,My Mega,SN-123,2.5.1");

        Assert.HasCount(5, fields);
        Assert.AreEqual("10", fields[0]);
        Assert.AreEqual("MobiFlight Mega", fields[1]);
        Assert.AreEqual("My Mega", fields[2]);
        Assert.AreEqual("SN-123", fields[3]);
        Assert.AreEqual("2.5.1", fields[4]);
    }

    [TestMethod]
    public void HonoursEscapedSeparators()
    {
        // A board named "Left, Panel" escapes the comma so it stays one field.
        var fields = MobiFlightBoardProbe.SplitFields(@"10,Type,Left\, Panel,SN-1,1.0.0");

        Assert.HasCount(5, fields);
        Assert.AreEqual("Left, Panel", fields[2]);
    }

    [TestMethod]
    public void HonoursEscapedEscapeCharacter()
    {
        var fields = MobiFlightBoardProbe.SplitFields(@"10,Type,back\\slash,SN-1,1.0.0");

        Assert.AreEqual(@"back\slash", fields[2]);
    }

    [TestMethod]
    public void HandlesTrailingEmptyField()
    {
        var fields = MobiFlightBoardProbe.SplitFields("10,Type,Name,SN-1,");

        Assert.HasCount(5, fields);
        Assert.AreEqual(string.Empty, fields[4]);
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
