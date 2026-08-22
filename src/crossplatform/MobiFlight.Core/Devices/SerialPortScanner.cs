using System.IO.Ports;
using System.Runtime.InteropServices;

namespace MobiFlight.Core.Devices;

/// <summary>
/// Enumerates serial ports in a way that makes sense on each platform.
/// </summary>
/// <remarks>
/// The Windows Connector discovers boards through WMI (System.Management), which does not exist on
/// macOS or Linux. System.IO.Ports.SerialPort.GetPortNames() works on all three, it just needs
/// per-platform filtering to be useful.
/// </remarks>
public static class SerialPortScanner
{
    /// <summary>
    /// Lists candidate serial ports, most likely to be a MobiFlight board first.
    /// </summary>
    /// <param name="includeUnlikely">
    /// Include ports that are almost never a board, such as built-in UARTs and Bluetooth devices.
    /// </param>
    public static IReadOnlyList<string> GetPortNames(bool includeUnlikely = false)
    {
        var ports = SerialPort.GetPortNames().Distinct(StringComparer.Ordinal).ToList();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // macOS exposes every device twice: /dev/tty.* blocks until carrier detect is asserted,
            // /dev/cu.* does not. Only the callout device is usable for a board.
            ports = ports.Where(p => !p.StartsWith("/dev/tty.", StringComparison.Ordinal)).ToList();
        }

        if (!includeUnlikely)
        {
            ports = ports.Where(IsLikelyBoard).ToList();
        }

        return ports.OrderByDescending(IsLikelyBoard)
                    .ThenBy(p => p, StringComparer.Ordinal)
                    .ToList();
    }

    /// <summary>
    /// Heuristic for "this could plausibly be an Arduino based board".
    /// </summary>
    public static bool IsLikelyBoard(string portName)
    {
        if (string.IsNullOrWhiteSpace(portName)) return false;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows gives no hint in the name itself.
            return portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // Bluetooth serial ports show up as /dev/cu.Bluetooth-*, which are never boards.
            if (portName.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)) return false;
            if (portName.Contains("debug-console", StringComparison.OrdinalIgnoreCase)) return false;

            return portName.Contains("usbmodem", StringComparison.OrdinalIgnoreCase)
                || portName.Contains("usbserial", StringComparison.OrdinalIgnoreCase)
                || portName.Contains("wchusbserial", StringComparison.OrdinalIgnoreCase)
                || portName.Contains("SLAB_USBtoUART", StringComparison.OrdinalIgnoreCase);
        }

        // Linux: ttyACM* is the CDC ACM class used by Leonardo/Micro/Pro Micro style boards,
        // ttyUSB* covers FTDI/CH340/CP210x adapters as used by Uno/Nano/Mega clones.
        return portName.StartsWith("/dev/ttyACM", StringComparison.Ordinal)
            || portName.StartsWith("/dev/ttyUSB", StringComparison.Ordinal);
    }

    /// <summary>
    /// Explains why no ports were found, with the platform specific fix.
    /// </summary>
    public static string GetTroubleshootingHint()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "On Linux the current user usually needs to be in the 'dialout' group to open a "
                 + "serial port: sudo usermod -aG dialout $USER (then log out and back in).";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "On macOS a USB serial adapter may need its vendor driver (CH340, CP210x). "
                 + "Boards using the built-in CDC class need no driver and appear as /dev/cu.usbmodem*.";
        }

        return "Check that the board is connected and that its driver is installed.";
    }
}
