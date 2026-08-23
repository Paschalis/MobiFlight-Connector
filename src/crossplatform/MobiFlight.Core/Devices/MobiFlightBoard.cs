using System.Globalization;

namespace MobiFlight.Core.Devices;

/// <summary>
/// The firmware command ids, matching MobiFlightModule.Command in the Windows Connector.
/// </summary>
public enum BoardCommand
{
    InitModule = 0,
    SetModule = 1,
    SetPin = 2,
    SetStepper = 3,
    SetServo = 4,
    Status = 5,
    EncoderChange = 6,
    ButtonChange = 7,
    StepperChange = 8,
    GetInfo = 9,
    Info = 10,
    SetConfig = 11,
    GetConfig = 12,
    ResetConfig = 13,
    SaveConfig = 14,
    ConfigSaved = 15,
    ActivateConfig = 16,
    ConfigActivated = 17,
    SetPowerSavingMode = 18,
    SetName = 19,
    GenNewSerial = 20,
    ResetStepper = 21,
    SetZeroStepper = 22,
    Retrigger = 23,
    ResetBoard = 24,
    SetLcdDisplayI2C = 25,
    SetModuleBrightness = 26,
    SetShiftRegisterPins = 27,
    AnalogChange = 28,
    InputShiftRegisterChange = 29,
    InputMultiplexerChange = 30,
    SetStepperSpeedAccel = 31,
    SetCustomDevice = 32,
    DebugPrint = 255,
}

public enum BoardInputType
{
    Button,
    Encoder,
    Analog,
}

public sealed class BoardInputEventArgs(string deviceName, BoardInputType type, int value) : EventArgs
{
    /// <summary>Device name as configured in the board's own EEPROM config.</summary>
    public string DeviceName { get; } = deviceName;

    public BoardInputType Type { get; } = type;

    /// <summary>
    /// Button: the press state. Encoder: the movement code. Analog: the raw reading.
    /// </summary>
    public int Value { get; } = value;

    public override string ToString() => $"{Type} {DeviceName} = {Value}";
}

/// <summary>
/// A connected MobiFlight board.
/// </summary>
/// <remarks>
/// Covers the output commands MobiFlight sends most often and reports button, encoder and analog
/// input back through <see cref="InputReceived"/>.
/// </remarks>
public sealed class MobiFlightBoard : IDisposable
{
    private readonly CmdMessengerChannel _channel;

    /// <summary>
    /// Remembers the last value written to each output so unchanged values are not resent. The
    /// firmware link is slow, and this is what the Windows Connector does too.
    /// </summary>
    private readonly Dictionary<string, string> _lastWritten = new(StringComparer.Ordinal);

    private MobiFlightBoard(CmdMessengerChannel channel, MobiFlightBoardInfo info)
    {
        _channel = channel;
        Info = info;

        _channel.MessageReceived += OnMessageReceived;
    }

    public MobiFlightBoardInfo Info { get; }

    public string Port => _channel.PortName;

    /// <summary>Raised when a button, encoder or analog input changes on the board.</summary>
    public event EventHandler<BoardInputEventArgs>? InputReceived;

    /// <summary>Raised for firmware debug output.</summary>
    public event EventHandler<string>? DebugMessage;

    /// <summary>
    /// Opens a port and identifies the board on it.
    /// </summary>
    /// <returns>The board, or null when nothing on that port speaks the protocol.</returns>
    public static async Task<MobiFlightBoard?> OpenAsync(
        string portName,
        int baudRate = MobiFlightBoardProbe.DefaultBaudRate,
        TimeSpan? timeout = null)
    {
        var wait = timeout ?? TimeSpan.FromSeconds(3);
        var channel = new CmdMessengerChannel(portName, baudRate);

        try
        {
            channel.Open();

            // A board that resets on DTR needs a moment before its sketch is listening.
            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);

            var info = await IdentifyAsync(channel, portName, wait).ConfigureAwait(false);
            if (info is null)
            {
                channel.Dispose();
                return null;
            }

            return new MobiFlightBoard(channel, info);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                      or IOException
                                      or InvalidOperationException
                                      or ArgumentException
                                      or TimeoutException)
        {
            channel.Dispose();
            return null;
        }
    }

    private static async Task<MobiFlightBoardInfo?> IdentifyAsync(
        CmdMessengerChannel channel,
        string portName,
        TimeSpan timeout)
    {
        // The first GetInfo after a reset is unreliable, so ask twice like the Connector does.
        var reply = await channel.SendAndWaitAsync((int)BoardCommand.GetInfo, (int)BoardCommand.Info, timeout)
                                 .ConfigureAwait(false)
                 ?? await channel.SendAndWaitAsync((int)BoardCommand.GetInfo, (int)BoardCommand.Info, timeout)
                                 .ConfigureAwait(false);

        if (reply is null || reply.Arguments.Count < 4) return null;

        return new MobiFlightBoardInfo(
            Port: portName,
            Type: reply.Argument(0),
            Name: reply.Argument(1),
            Serial: reply.Argument(2),
            Version: reply.Argument(3),
            CoreVersion: reply.Arguments.Count > 4 ? reply.Argument(4) : null);
    }

    // ------------------------------------------------------------------ outputs

    /// <summary>
    /// Drives a single output pin.
    /// </summary>
    public void SetPin(int pin, int value)
    {
        if (!HasChanged($"pin:{pin}", value.ToString(CultureInfo.InvariantCulture))) return;

        _channel.Send((int)BoardCommand.SetPin, pin, value);
    }

    /// <summary>
    /// Writes to a 7-segment display module.
    /// </summary>
    /// <param name="module">Display module number as configured on the board.</param>
    /// <param name="subModule">Sub-module, only meaningful for MAX72xx chains.</param>
    /// <param name="value">Digits to show, most significant first.</param>
    /// <param name="decimalPoints">Bitmask of which digits get a decimal point.</param>
    /// <param name="mask">Bitmask of which digits this write affects.</param>
    public void SetDisplay(int module, int subModule, string value, byte decimalPoints = 0, byte mask = 0xFF)
    {
        var key = $"display:{module}:{subModule}";
        if (!HasChanged(key, $"{value}|{decimalPoints}|{mask}")) return;

        _channel.Send((int)BoardCommand.SetModule, module, subModule, value, decimalPoints, mask);
    }

    /// <summary>
    /// Sets display brightness, 0 to 15.
    /// </summary>
    public void SetDisplayBrightness(int module, int subModule, int brightness)
    {
        var clamped = Math.Clamp(brightness, 0, 15);
        if (!HasChanged($"brightness:{module}:{subModule}", clamped.ToString(CultureInfo.InvariantCulture))) return;

        _channel.Send((int)BoardCommand.SetModuleBrightness, module, subModule, clamped);
    }

    /// <summary>
    /// Moves a servo to an absolute position.
    /// </summary>
    public void SetServo(int servo, int value, int min = 0, int max = 180, int maxRotationPercent = 100)
    {
        if (!HasChanged($"servo:{servo}", value.ToString(CultureInfo.InvariantCulture))) return;

        _channel.Send((int)BoardCommand.SetServo, servo, value, min, max, maxRotationPercent);
    }

    /// <summary>
    /// Moves a stepper to an absolute position.
    /// </summary>
    public void SetStepper(int stepper, int value, int inputRevolutionSteps = -1)
    {
        if (!HasChanged($"stepper:{stepper}", value.ToString(CultureInfo.InvariantCulture))) return;

        _channel.Send((int)BoardCommand.SetStepper, stepper, value, inputRevolutionSteps);
    }

    /// <summary>
    /// Declares the stepper's current position to be zero.
    /// </summary>
    public void SetStepperZero(int stepper)
    {
        _lastWritten.Remove($"stepper:{stepper}");
        _channel.Send((int)BoardCommand.SetZeroStepper, stepper);
    }

    /// <summary>
    /// Writes text to an I2C character LCD.
    /// </summary>
    public void SetLcdDisplay(int address, string text)
    {
        if (!HasChanged($"lcd:{address}", text)) return;

        _channel.Send((int)BoardCommand.SetLcdDisplayI2C, address, text);
    }

    /// <summary>
    /// Sets the outputs of a shift register.
    /// </summary>
    public void SetShiftRegisterPins(int register, string pins, int value)
    {
        if (!HasChanged($"shift:{register}:{pins}", value.ToString(CultureInfo.InvariantCulture))) return;

        _channel.Send((int)BoardCommand.SetShiftRegisterPins, register, pins, value);
    }

    /// <summary>
    /// Turns power saving on or off. Boards blank their displays while saving.
    /// </summary>
    public void SetPowerSavingMode(bool enabled)
    {
        _channel.Send((int)BoardCommand.SetPowerSavingMode, enabled ? 1 : 0);

        // Displays are blanked by the firmware, so the cached values no longer reflect reality.
        if (enabled) _lastWritten.Clear();
    }

    /// <summary>
    /// Forgets cached output values so the next write is always sent.
    /// </summary>
    public void InvalidateCache() => _lastWritten.Clear();

    private bool HasChanged(string key, string value)
    {
        if (_lastWritten.TryGetValue(key, out var previous) && previous == value) return false;

        _lastWritten[key] = value;
        return true;
    }

    // ------------------------------------------------------------------- inputs

    private void OnMessageReceived(object? sender, CmdMessage message)
    {
        switch ((BoardCommand)message.CommandId)
        {
            case BoardCommand.ButtonChange:
                InputReceived?.Invoke(this, new BoardInputEventArgs(
                    message.Argument(0), BoardInputType.Button, message.ArgumentAsInt(1)));
                return;

            case BoardCommand.EncoderChange:
                InputReceived?.Invoke(this, new BoardInputEventArgs(
                    message.Argument(0), BoardInputType.Encoder, message.ArgumentAsInt(1)));
                return;

            case BoardCommand.AnalogChange:
                InputReceived?.Invoke(this, new BoardInputEventArgs(
                    message.Argument(0), BoardInputType.Analog, message.ArgumentAsInt(1)));
                return;

            case BoardCommand.DebugPrint:
                DebugMessage?.Invoke(this, message.Argument(0));
                return;
        }
    }

    public void Dispose()
    {
        _channel.MessageReceived -= OnMessageReceived;
        _channel.Dispose();
    }
}
