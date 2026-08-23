using System.Globalization;
using MobiFlight.Core.Devices;
using MobiFlight.Core.Xplane;

namespace MobiFlight.Core.Project;

/// <summary>
/// Runs a MobiFlight project: X-Plane datarefs drive board outputs, board inputs drive X-Plane.
/// </summary>
public sealed class ConfigRunner : IAsyncDisposable
{
    private readonly McConfigProject _project;
    private readonly XplaneUdpClient _xplane;
    private readonly IReadOnlyList<MobiFlightBoard> _boards;

    /// <summary>Output configs grouped by the dataref that feeds them.</summary>
    private readonly Dictionary<string, List<OutputConfig>> _outputsByDataRef = new(StringComparer.Ordinal);

    /// <summary>Input configs grouped by board serial and device name.</summary>
    private readonly Dictionary<string, List<InputConfig>> _inputsByDevice = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Output configs whose source is a MobiFlight variable rather than the sim.</summary>
    private readonly List<OutputConfig> _variableDrivenOutputs = [];

    private bool _started;

    public ConfigRunner(McConfigProject project, XplaneUdpClient xplane, IReadOnlyList<MobiFlightBoard> boards)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _xplane = xplane ?? throw new ArgumentNullException(nameof(xplane));
        _boards = boards ?? throw new ArgumentNullException(nameof(boards));
    }

    /// <summary>Raised for human readable progress and diagnostics.</summary>
    public event EventHandler<string>? Log;

    /// <summary>Number of output updates pushed to a board.</summary>
    public int OutputsWritten { get; private set; }

    /// <summary>Number of input events forwarded to X-Plane.</summary>
    public int InputsForwarded { get; private set; }

    /// <summary>Output configs that were skipped, with the reason.</summary>
    public List<string> Skipped { get; } = [];

    /// <summary>Number of times a config was held back because its preconditions did not hold.</summary>
    public int PreconditionBlocks { get; private set; }

    /// <summary>Runtime state: config values and MobiFlight variables.</summary>
    public ConfigValueStore Values { get; } = new();

    /// <summary>
    /// Wires everything up and subscribes to the datarefs the project needs.
    /// </summary>
    public async Task StartAsync(int frequency = 10, CancellationToken cancellationToken = default)
    {
        if (_started) throw new InvalidOperationException("Runner already started.");
        _started = true;

        BuildOutputIndex();
        BuildInputIndex();

        _xplane.DataRefChanged += OnDataRefChanged;

        foreach (var board in _boards)
        {
            board.InputReceived += OnBoardInput;
        }

        foreach (var dataRef in _outputsByDataRef.Keys)
        {
            await _xplane.SubscribeAsync(dataRef, frequency, cancellationToken).ConfigureAwait(false);
        }

        Log?.Invoke(this, $"Subscribed to {_outputsByDataRef.Count} dataref(s) at {frequency} Hz.");
        Log?.Invoke(this, $"Listening to {_inputsByDevice.Count} input device binding(s).");

        if (_variableDrivenOutputs.Count > 0)
        {
            Log?.Invoke(this, $"{_variableDrivenOutputs.Count} output(s) driven by MobiFlight variables.");
        }
    }

    private void BuildOutputIndex()
    {
        foreach (var output in _project.Outputs)
        {
            if (!output.Active) continue;

            // A variable driven output does not listen to the sim; it is re-evaluated whenever the
            // variable it reads is written.
            if (output.SourceKind == ConfigSourceKind.Variable)
            {
                if (string.IsNullOrWhiteSpace(output.VariableName))
                {
                    Skipped.Add($"{output.Description}: no variable name configured.");
                    continue;
                }

                _variableDrivenOutputs.Add(output);
                continue;
            }

            if (output.SourceKind != ConfigSourceKind.XplaneDataRef)
            {
                Skipped.Add($"{output.Description}: source '{output.RawSourceType}' is not available outside Windows.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(output.DataRef))
            {
                Skipped.Add($"{output.Description}: no dataref configured.");
                continue;
            }

            if (output.DeviceKind == OutputDeviceKind.Unknown)
            {
                Skipped.Add($"{output.Description}: unsupported output device.");
                continue;
            }

            if (ResolveBoard(output.BoardSerial) is null)
            {
                Skipped.Add($"{output.Description}: board '{output.BoardSerial}' is not connected.");
                continue;
            }

            if (output.Preconditions.Any(p => p.Active && p.Kind == PreconditionKind.Pin))
            {
                Skipped.Add($"{output.Description}: depends on an Arcaze pin, which is Windows only.");
                continue;
            }

            if (!_outputsByDataRef.TryGetValue(output.DataRef, out var list))
            {
                list = [];
                _outputsByDataRef[output.DataRef] = list;
            }

            list.Add(output);
        }
    }

    private void BuildInputIndex()
    {
        foreach (var input in _project.Inputs)
        {
            if (!input.IsExecutable) continue;
            if (ResolveBoard(input.BoardSerial) is null) continue;

            var key = DeviceKey(input.BoardSerial, input.DeviceName);

            if (!_inputsByDevice.TryGetValue(key, out var list))
            {
                list = [];
                _inputsByDevice[key] = list;
            }

            list.Add(input);
        }
    }

    // --------------------------------------------------------------- sim to board

    private void OnDataRefChanged(object? sender, XplaneDataRefChangedEventArgs e)
    {
        if (!_outputsByDataRef.TryGetValue(e.DataRef, out var configs)) return;

        foreach (var config in configs)
        {
            try
            {
                Apply(config, e.Value);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
            {
                Log?.Invoke(this, $"Could not update '{config.Description}': {ex.Message}");
            }
        }
    }

    private void Apply(OutputConfig config, double rawValue)
    {
        var placeholders = Values.ResolvePlaceholders(config.ConfigReferences);

        var value = Evaluate(config, rawValue, placeholders);
        if (value is null) return;

        // Remembered even when the preconditions block the write, because other configs may
        // reference this value or test it in their own preconditions.
        Values.SetConfigValue(config.Guid, value.Value);

        if (!Values.ArePreconditionsMet(config.Preconditions))
        {
            PreconditionBlocks++;
            return;
        }

        // A variable source with no device exists purely to compute a value for others to use.
        if (config.DeviceKind == OutputDeviceKind.Unknown) return;

        var board = ResolveBoard(config.BoardSerial);
        if (board is null) return;

        switch (config.DeviceKind)
        {
            case OutputDeviceKind.Pin:
                // Anything non-zero lights the pin.
                board.SetPin(config.Pin, value.Value != 0 ? 1 : 0);
                break;

            case OutputDeviceKind.DisplayModule:
                board.SetDisplay(
                    ParseAddress(config.DisplayAddress),
                    config.DisplayConnector,
                    FormatForDisplay(config, value.Value),
                    BuildDigitMask(config.DisplayDecimalPoints),
                    BuildDigitMask(config.DisplayDigits, fallback: 0xFF));
                break;

            case OutputDeviceKind.Servo:
                board.SetServo(
                    config.ServoAddress,
                    (int)Math.Round(value.Value),
                    config.ServoMin,
                    config.ServoMax,
                    config.ServoMaxRotation);
                break;

            case OutputDeviceKind.Stepper:
                board.SetStepper(config.ServoAddress, (int)Math.Round(value.Value));
                break;

            case OutputDeviceKind.LcdDisplay:
                board.SetLcdDisplay(
                    ParseAddress(config.DisplayAddress),
                    value.Value.ToString(CultureInfo.InvariantCulture));
                break;

            default:
                return;
        }

        OutputsWritten++;
    }

    /// <summary>
    /// Applies the transformation and then the comparison, matching the Connector's order.
    /// </summary>
    internal static double? Evaluate(
        OutputConfig config,
        double rawValue,
        IReadOnlyDictionary<string, double>? placeholders = null)
    {
        var value = rawValue;

        if (config.Transformation.Active && !string.IsNullOrWhiteSpace(config.Transformation.Expression))
        {
            var transformed = ExpressionEvaluator.Evaluate(
                config.Transformation.Expression, value, null, placeholders);
            if (transformed is null) return null;

            value = transformed.Value;
        }

        if (!config.Comparison.Active) return value;

        var matches = CompareTo(value, config.Comparison);
        var branch = matches ? config.Comparison.IfValue : config.Comparison.ElseValue;

        // An empty branch means "leave the value alone".
        if (string.IsNullOrWhiteSpace(branch)) return value;

        return ExpressionEvaluator.Evaluate(branch, value, null, placeholders);
    }

    private static bool CompareTo(double value, ComparisonRule rule)
    {
        if (!double.TryParse(rule.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var reference))
        {
            return false;
        }

        return rule.Operand switch
        {
            "=" or "==" => Math.Abs(value - reference) < double.Epsilon,
            "!=" or "<>" => Math.Abs(value - reference) >= double.Epsilon,
            ">" => value > reference,
            "<" => value < reference,
            ">=" => value >= reference,
            "<=" => value <= reference,
            _ => false,
        };
    }

    // --------------------------------------------------------------- board to sim

    private void OnBoardInput(object? sender, BoardInputEventArgs e)
    {
        if (sender is not MobiFlightBoard board) return;

        var key = DeviceKey(board.Info.Serial, e.DeviceName);
        if (!_inputsByDevice.TryGetValue(key, out var configs)) return;

        foreach (var config in configs)
        {
            var eventName = ResolveEventName(config, e);
            if (eventName is null) continue;

            if (!config.Actions.TryGetValue(eventName, out var action)) continue;
            if (action.Kind == InputActionKind.Unsupported) continue;
            if (string.IsNullOrWhiteSpace(action.Path)) continue;

            if (!Values.ArePreconditionsMet(config.Preconditions))
            {
                PreconditionBlocks++;
                continue;
            }

            _ = ForwardAsync(action, e.Value, Values.ResolvePlaceholders(config.ConfigReferences));
        }
    }

    /// <summary>
    /// Maps a raw firmware event value onto the config's event name.
    /// </summary>
    /// <remarks>
    /// Button: 0 press, 1 release, 2 long release. Encoder: 0 left, 1 left fast, 2 right,
    /// 3 right fast, with the fast variants falling back to the slow ones when unbound.
    /// </remarks>
    internal static string? ResolveEventName(InputConfig config, BoardInputEventArgs e)
    {
        switch (e.Type)
        {
            case BoardInputType.Button:
                return e.Value switch
                {
                    0 => "onPress",
                    1 => "onRelease",
                    2 => config.Actions.ContainsKey("onLongRelease") ? "onLongRelease" : "onRelease",
                    _ => null,
                };

            case BoardInputType.Encoder:
                return e.Value switch
                {
                    0 => "onLeft",
                    1 => config.Actions.ContainsKey("onLeftFast") ? "onLeftFast" : "onLeft",
                    2 => "onRight",
                    3 => config.Actions.ContainsKey("onRightFast") ? "onRightFast" : "onRight",
                    _ => null,
                };

            case BoardInputType.Analog:
                return "onChange";

            default:
                return null;
        }
    }

    private async Task ForwardAsync(
        InputAction action,
        int triggerValue,
        IReadOnlyDictionary<string, double> placeholders)
    {
        try
        {
            if (action.Kind == InputActionKind.XplaneCommand)
            {
                await _xplane.SendCommandAsync(action.Path!).ConfigureAwait(false);
                InputsForwarded++;
                return;
            }

            if (action.Kind == InputActionKind.SetVariable)
            {
                // A variable expression sees the variable's own current value as $.
                var previous = Values.GetVariableNumber(action.Path!) ?? 0;
                var updated = ExpressionEvaluator.Evaluate(action.Expression, previous, triggerValue, placeholders);

                if (updated is null)
                {
                    Log?.Invoke(this, $"Could not evaluate '{action.Expression}' for variable {action.Path}.");
                    return;
                }

                Values.SetVariable(action.Path!, updated.Value);
                InputsForwarded++;

                // Outputs reading this variable have to be recomputed now, since nothing in the sim
                // will tell them the value moved.
                RefreshVariableDrivenOutputs(action.Path!);
                return;
            }

            // A dataref write may reference the dataref's own current value through $.
            var current = _xplane.ReadDataRef(action.Path!) ?? 0;
            var result = ExpressionEvaluator.Evaluate(action.Expression, current, triggerValue, placeholders);

            if (result is null)
            {
                Log?.Invoke(this, $"Could not evaluate '{action.Expression}' for {action.Path}.");
                return;
            }

            await _xplane.WriteDataRefAsync(action.Path!, (float)result.Value).ConfigureAwait(false);
            InputsForwarded++;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Log?.Invoke(this, $"Could not forward input to X-Plane: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-applies every output that reads the given variable.
    /// </summary>
    private void RefreshVariableDrivenOutputs(string variableName)
    {
        foreach (var config in _variableDrivenOutputs)
        {
            if (!string.Equals(config.VariableName, variableName, StringComparison.OrdinalIgnoreCase)) continue;

            var value = Values.GetVariableNumber(variableName);
            if (value is null) continue;

            try
            {
                Apply(config, value.Value);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
            {
                Log?.Invoke(this, $"Could not update '{config.Description}': {ex.Message}");
            }
        }
    }

    // ------------------------------------------------------------------- helpers

    /// <summary>
    /// Finds the connected board a config refers to.
    /// </summary>
    /// <remarks>
    /// Config files store the board as "Name/ Serial", so both halves are accepted.
    /// </remarks>
    internal MobiFlightBoard? ResolveBoard(string? configSerial)
    {
        if (string.IsNullOrWhiteSpace(configSerial)) return _boards.Count == 1 ? _boards[0] : null;

        var serial = ExtractSerial(configSerial);

        return _boards.FirstOrDefault(b =>
                   string.Equals(b.Info.Serial, serial, StringComparison.OrdinalIgnoreCase))
            ?? _boards.FirstOrDefault(b =>
                   string.Equals(b.Info.Name, configSerial.Split('/')[0].Trim(), StringComparison.OrdinalIgnoreCase));
    }

    internal static string ExtractSerial(string configSerial)
    {
        var slash = configSerial.LastIndexOf('/');
        return slash >= 0 ? configSerial[(slash + 1)..].Trim() : configSerial.Trim();
    }

    private static string DeviceKey(string? serial, string deviceName)
    {
        return $"{ExtractSerial(serial ?? string.Empty)}|{deviceName}";
    }

    private static int ParseAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return 0;

        // Values look like "LedModule" or a plain number depending on the config version.
        return int.TryParse(address, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    /// <summary>
    /// Turns a digit list such as "2,3,4,5,6" into the bitmask the firmware expects.
    /// </summary>
    internal static byte BuildDigitMask(string? digits, byte fallback = 0)
    {
        if (string.IsNullOrWhiteSpace(digits)) return fallback;

        byte mask = 0;
        foreach (var part in digits.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var digit)
                && digit is >= 0 and < 8)
            {
                mask |= (byte)(1 << digit);
            }
        }

        return mask == 0 ? fallback : mask;
    }

    /// <summary>
    /// Renders a numeric value for a 7-segment display, honouring the configured padding.
    /// </summary>
    internal static string FormatForDisplay(OutputConfig config, double value)
    {
        var text = value == Math.Floor(value) && Math.Abs(value) < 1e9
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.####", CultureInfo.InvariantCulture);

        if (!config.DisplayPadding) return text;

        var width = CountDigits(config.DisplayDigits);
        if (width <= 0) return text;

        var paddingChar = string.IsNullOrEmpty(config.DisplayPaddingChar) ? '0' : config.DisplayPaddingChar[0];

        return text.Length >= width ? text : text.PadLeft(width, paddingChar);
    }

    private static int CountDigits(string? digits)
    {
        return string.IsNullOrWhiteSpace(digits)
            ? 0
            : digits.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
    }

    public ValueTask DisposeAsync()
    {
        if (_started)
        {
            _xplane.DataRefChanged -= OnDataRefChanged;

            foreach (var board in _boards)
            {
                board.InputReceived -= OnBoardInput;
            }
        }

        return ValueTask.CompletedTask;
    }
}
