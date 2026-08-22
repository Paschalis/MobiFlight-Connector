using System.IO.Ports;
using System.Text;

namespace MobiFlight.Core.Devices;

/// <summary>
/// What a MobiFlight board reports about itself.
/// </summary>
public sealed record MobiFlightBoardInfo(
    string Port,
    string Type,
    string Name,
    string Serial,
    string Version,
    string? CoreVersion);

/// <summary>
/// Asks a serial port whether a MobiFlight board is on the other end.
/// </summary>
/// <remarks>
/// Implements just enough of the CmdMessenger framing used by the MobiFlight firmware: fields are
/// separated by ',', a message ends with ';', and both can be escaped with '\'. The probe sends
/// GetInfo (command 9) and expects Info (command 10) back.
/// </remarks>
public static class MobiFlightBoardProbe
{
    private const int GetInfoCommand = 9;
    private const int InfoCommand = 10;

    private const char FieldSeparator = ',';
    private const char CommandSeparator = ';';
    private const char EscapeCharacter = '\\';

    /// <summary>Baud rate used by the MobiFlight firmware.</summary>
    public const int DefaultBaudRate = 115200;

    /// <summary>
    /// Probes a single port.
    /// </summary>
    /// <returns>The board info, or null when nothing answered.</returns>
    public static MobiFlightBoardInfo? Probe(
        string portName,
        int baudRate = DefaultBaudRate,
        TimeSpan? timeout = null)
    {
        var readTimeout = timeout ?? TimeSpan.FromSeconds(3);

        try
        {
            using var port = new SerialPort(portName, baudRate)
            {
                ReadTimeout = (int)readTimeout.TotalMilliseconds,
                WriteTimeout = (int)readTimeout.TotalMilliseconds,
                // Boards based on the ATmega32u4 (Leonardo, Micro, Pro Micro) reset when DTR
                // toggles. Asserting it is what the Windows Connector does as well.
                DtrEnable = true,
            };

            port.Open();

            // Give a resetting bootloader time to hand over to the sketch.
            Thread.Sleep(TimeSpan.FromMilliseconds(500));
            port.DiscardInBuffer();

            port.Write($"{GetInfoCommand}{CommandSeparator}");

            var response = ReadMessage(port, readTimeout);
            if (response is null) return null;

            var fields = SplitFields(response);

            // fields[0] is the command id, then type, name, serial, version and optionally core version.
            if (fields.Count < 5) return null;
            if (!int.TryParse(fields[0], out var commandId) || commandId != InfoCommand) return null;

            return new MobiFlightBoardInfo(
                Port: portName,
                Type: fields[1],
                Name: fields[2],
                Serial: fields[3],
                Version: fields[4],
                CoreVersion: fields.Count > 5 ? fields[5] : null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                      or IOException
                                      or TimeoutException
                                      or InvalidOperationException
                                      or ArgumentException)
        {
            // Port busy, permission denied, or nothing that speaks the protocol.
            return null;
        }
    }

    /// <summary>
    /// Probes every likely port and returns the boards that answered.
    /// </summary>
    public static IReadOnlyList<MobiFlightBoardInfo> ProbeAll(
        IEnumerable<string>? portNames = null,
        int baudRate = DefaultBaudRate,
        TimeSpan? timeout = null)
    {
        var ports = portNames?.ToList() ?? SerialPortScanner.GetPortNames().ToList();

        var found = new List<MobiFlightBoardInfo>();
        foreach (var port in ports)
        {
            var info = Probe(port, baudRate, timeout);
            if (info is not null) found.Add(info);
        }

        return found;
    }

    /// <summary>
    /// Reads characters until an unescaped command separator arrives.
    /// </summary>
    private static string? ReadMessage(SerialPort port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var builder = new StringBuilder();
        var escaped = false;

        while (DateTime.UtcNow < deadline)
        {
            int next;
            try
            {
                next = port.ReadChar();
            }
            catch (TimeoutException)
            {
                return null;
            }

            var character = (char)next;

            if (escaped)
            {
                builder.Append(character);
                escaped = false;
                continue;
            }

            if (character == EscapeCharacter)
            {
                escaped = true;
                continue;
            }

            if (character == CommandSeparator)
            {
                return builder.ToString();
            }

            builder.Append(character);
        }

        return null;
    }

    /// <summary>
    /// Splits a message body on unescaped field separators.
    /// </summary>
    internal static List<string> SplitFields(string message)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var escaped = false;

        foreach (var character in message)
        {
            if (escaped)
            {
                current.Append(character);
                escaped = false;
                continue;
            }

            if (character == EscapeCharacter)
            {
                escaped = true;
                continue;
            }

            if (character == FieldSeparator)
            {
                fields.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(character);
        }

        fields.Add(current.ToString());
        return fields;
    }
}
