namespace MobiFlight.Core.Project;

/// <summary>
/// Where an output config reads its value from.
/// </summary>
public enum ConfigSourceKind
{
    /// <summary>An X-Plane dataref. The only source the portable build can serve.</summary>
    XplaneDataRef,

    /// <summary>FSUIPC, SimConnect, ProSim or anything else. Recorded but not executable here.</summary>
    Unsupported,
}

/// <summary>
/// What an output config drives on the board.
/// </summary>
public enum OutputDeviceKind
{
    Pin,
    DisplayModule,
    Servo,
    Stepper,
    LcdDisplay,
    Unknown,
}

/// <summary>
/// The if/else step applied to a value before it reaches the device.
/// </summary>
public sealed record ComparisonRule(
    bool Active,
    string? Operand,
    string? Value,
    string? IfValue,
    string? ElseValue);

/// <summary>
/// The expression applied to a value before comparison.
/// </summary>
public sealed record TransformationRule(bool Active, string? Expression);

/// <summary>
/// Everything needed to drive one output.
/// </summary>
public sealed record OutputConfig
{
    public required string Guid { get; init; }
    public required string Description { get; init; }
    public bool Active { get; init; }

    public ConfigSourceKind SourceKind { get; init; }

    /// <summary>Dataref path when <see cref="SourceKind"/> is XplaneDataRef.</summary>
    public string? DataRef { get; init; }

    /// <summary>Raw source type as written in the file, for diagnostics.</summary>
    public string? RawSourceType { get; init; }

    public OutputDeviceKind DeviceKind { get; init; }

    /// <summary>Serial of the board this output belongs to.</summary>
    public string? BoardSerial { get; init; }

    /// <summary>Pin number for <see cref="OutputDeviceKind.Pin"/>.</summary>
    public int Pin { get; init; }

    /// <summary>Display module address, sub-module and digit layout.</summary>
    public string? DisplayAddress { get; init; }
    public int DisplayConnector { get; init; }
    public string? DisplayDigits { get; init; }
    public string? DisplayDecimalPoints { get; init; }
    public bool DisplayPadding { get; init; }
    public string? DisplayPaddingChar { get; init; }

    /// <summary>Servo and stepper addressing.</summary>
    public int ServoAddress { get; init; }
    public int ServoMin { get; init; }
    public int ServoMax { get; init; }
    public int ServoMaxRotation { get; init; } = 100;

    public TransformationRule Transformation { get; init; } = new(false, "$");
    public ComparisonRule Comparison { get; init; } = new(false, null, null, null, null);

    /// <summary>True when this config can actually run in the portable build.</summary>
    public bool IsExecutable => Active
                             && SourceKind == ConfigSourceKind.XplaneDataRef
                             && !string.IsNullOrWhiteSpace(DataRef)
                             && DeviceKind != OutputDeviceKind.Unknown;
}

/// <summary>
/// What an input action does when it fires.
/// </summary>
public enum InputActionKind
{
    /// <summary>Write a value into an X-Plane dataref.</summary>
    XplaneDataRef,

    /// <summary>Trigger an X-Plane command.</summary>
    XplaneCommand,

    /// <summary>FSUIPC, key presses and everything else the portable build cannot run.</summary>
    Unsupported,
}

/// <summary>
/// One action bound to one event on an input device.
/// </summary>
public sealed record InputAction(
    InputActionKind Kind,
    string? Path,
    string? Expression,
    string? RawType);

/// <summary>
/// The kind of input device a config is bound to.
/// </summary>
public enum InputDeviceKind
{
    Button,
    Encoder,
    AnalogInput,
    Unknown,
}

/// <summary>
/// A board input and the actions bound to its events.
/// </summary>
public sealed record InputConfig
{
    public required string Guid { get; init; }
    public required string Description { get; init; }
    public bool Active { get; init; }

    /// <summary>Serial of the board the input belongs to.</summary>
    public string? BoardSerial { get; init; }

    /// <summary>Device name as configured on the board.</summary>
    public required string DeviceName { get; init; }

    public InputDeviceKind DeviceKind { get; init; }

    /// <summary>
    /// Actions by event name: "onPress", "onRelease", "onLeft", "onRight", "onLeftFast",
    /// "onRightFast", "onChange".
    /// </summary>
    public IReadOnlyDictionary<string, InputAction> Actions { get; init; }
        = new Dictionary<string, InputAction>(StringComparer.OrdinalIgnoreCase);

    public bool IsExecutable => Active
                             && Actions.Values.Any(a => a.Kind != InputActionKind.Unsupported);
}

/// <summary>
/// A parsed MobiFlight project file.
/// </summary>
public sealed record McConfigProject
{
    public required string Path { get; init; }
    public IReadOnlyList<OutputConfig> Outputs { get; init; } = [];
    public IReadOnlyList<InputConfig> Inputs { get; init; } = [];

    /// <summary>Configs that reference a sim the portable build cannot talk to.</summary>
    public IEnumerable<OutputConfig> UnsupportedOutputs =>
        Outputs.Where(o => o.Active && o.SourceKind != ConfigSourceKind.XplaneDataRef);

    public IEnumerable<InputConfig> UnsupportedInputs =>
        Inputs.Where(i => i.Active && !i.IsExecutable);
}
