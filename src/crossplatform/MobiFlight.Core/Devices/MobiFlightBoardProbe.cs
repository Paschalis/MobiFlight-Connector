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
/// Finds MobiFlight boards on the serial ports of this machine.
/// </summary>
public static class MobiFlightBoardProbe
{
    /// <summary>Baud rate used by the MobiFlight firmware.</summary>
    public const int DefaultBaudRate = 115200;

    /// <summary>
    /// Asks a single port whether a MobiFlight board answers on it.
    /// </summary>
    /// <returns>The board info, or null when nothing answered.</returns>
    public static async Task<MobiFlightBoardInfo?> ProbeAsync(
        string portName,
        int baudRate = DefaultBaudRate,
        TimeSpan? timeout = null)
    {
        using var board = await MobiFlightBoard.OpenAsync(portName, baudRate, timeout).ConfigureAwait(false);

        return board?.Info;
    }

    /// <summary>
    /// Probes every likely port and returns the boards that answered.
    /// </summary>
    public static async Task<IReadOnlyList<MobiFlightBoardInfo>> ProbeAllAsync(
        IEnumerable<string>? portNames = null,
        int baudRate = DefaultBaudRate,
        TimeSpan? timeout = null)
    {
        var ports = portNames?.ToList() ?? SerialPortScanner.GetPortNames().ToList();

        var found = new List<MobiFlightBoardInfo>();
        foreach (var port in ports)
        {
            var info = await ProbeAsync(port, baudRate, timeout).ConfigureAwait(false);
            if (info is not null) found.Add(info);
        }

        return found;
    }

    /// <summary>
    /// Opens every board that answers, ready to be driven.
    /// </summary>
    public static async Task<IReadOnlyList<MobiFlightBoard>> OpenAllAsync(
        IEnumerable<string>? portNames = null,
        int baudRate = DefaultBaudRate,
        TimeSpan? timeout = null)
    {
        var ports = portNames?.ToList() ?? SerialPortScanner.GetPortNames().ToList();

        var boards = new List<MobiFlightBoard>();
        foreach (var port in ports)
        {
            var board = await MobiFlightBoard.OpenAsync(port, baudRate, timeout).ConfigureAwait(false);
            if (board is not null) boards.Add(board);
        }

        return boards;
    }
}
