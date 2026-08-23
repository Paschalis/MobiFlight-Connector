using System.Globalization;
using System.Xml.Linq;

namespace MobiFlight.Core.Project;

/// <summary>
/// Reads MobiFlight .mcc project files.
/// </summary>
/// <remarks>
/// The Windows Connector deserializes these through a typed DataSet. Here they are read with
/// LINQ to XML, taking only the parts the portable build can act on and recording the rest so it
/// can be reported rather than silently dropped.
/// </remarks>
public static class McConfigReader
{
    public static McConfigProject Load(string path)
    {
        var document = XDocument.Load(path);
        return Parse(document, path);
    }

    public static McConfigProject ParseXml(string xml, string path = "<memory>")
    {
        return Parse(XDocument.Parse(xml), path);
    }

    private static McConfigProject Parse(XDocument document, string path)
    {
        var root = document.Root ?? throw new InvalidDataException("The project file is empty.");

        return new McConfigProject
        {
            Path = path,
            Outputs = root.Element("outputs")?.Elements("config").Select(ReadOutput).ToList() ?? [],
            Inputs = root.Element("inputs")?.Elements("config").Select(ReadInput).ToList() ?? [],
        };
    }

    private static OutputConfig ReadOutput(XElement config)
    {
        var settings = config.Element("settings");
        var source = settings?.Element("source");
        var display = settings?.Element("display");
        var comparison = settings?.Element("comparison");
        var transformation = settings?.Element("transformation");

        var sourceType = source?.Attribute("type")?.Value;
        var displayType = display?.Attribute("type")?.Value;

        return new OutputConfig
        {
            Guid = config.Attribute("guid")?.Value ?? System.Guid.NewGuid().ToString(),
            Description = config.Element("description")?.Value ?? string.Empty,
            Active = ReadBool(config.Element("active")?.Value, false),

            RawSourceType = sourceType,
            SourceKind = sourceType == "XplaneDataRef" ? ConfigSourceKind.XplaneDataRef : ConfigSourceKind.Unsupported,
            DataRef = source?.Attribute("path")?.Value,

            BoardSerial = display?.Attribute("serial")?.Value,
            DeviceKind = ReadDeviceKind(displayType),

            Pin = ReadInt(display?.Attribute("pin")?.Value),

            DisplayAddress = display?.Attribute("ledAddress")?.Value,
            DisplayConnector = ReadInt(display?.Attribute("ledConnector")?.Value),
            DisplayDigits = display?.Attribute("ledDigits")?.Value,
            DisplayDecimalPoints = display?.Attribute("ledDecimalPoints")?.Value,
            DisplayPadding = ReadBool(display?.Attribute("ledPadding")?.Value, false),
            DisplayPaddingChar = display?.Attribute("ledPaddingChar")?.Value,

            ServoAddress = ReadInt(display?.Attribute("servoAddress")?.Value),
            ServoMin = ReadInt(display?.Attribute("servoMin")?.Value),
            ServoMax = ReadInt(display?.Attribute("servoMax")?.Value, 180),
            ServoMaxRotation = ReadInt(display?.Attribute("servoMaxRotationPercent")?.Value, 100),

            Transformation = new TransformationRule(
                ReadBool(transformation?.Attribute("active")?.Value, false),
                transformation?.Attribute("expression")?.Value),

            Comparison = new ComparisonRule(
                ReadBool(comparison?.Attribute("active")?.Value, false),
                comparison?.Attribute("operand")?.Value,
                comparison?.Attribute("value")?.Value,
                comparison?.Attribute("ifValue")?.Value,
                comparison?.Attribute("elseValue")?.Value),
        };
    }

    private static InputConfig ReadInput(XElement config)
    {
        var settings = config.Element("settings");

        // The device element is named after its kind: <button>, <encoder>, <analog>.
        var deviceElement = settings?.Elements()
            .FirstOrDefault(e => e.Name.LocalName is "button" or "encoder" or "analog");

        var actions = new Dictionary<string, InputAction>(StringComparer.OrdinalIgnoreCase);

        foreach (var action in deviceElement?.Elements() ?? [])
        {
            var parsed = ReadAction(action);
            if (parsed is not null) actions[action.Name.LocalName] = parsed;
        }

        return new InputConfig
        {
            Guid = config.Attribute("guid")?.Value ?? System.Guid.NewGuid().ToString(),
            Description = config.Element("description")?.Value ?? string.Empty,
            Active = ReadBool(config.Element("active")?.Value, false),

            BoardSerial = settings?.Attribute("serial")?.Value,
            DeviceName = settings?.Attribute("name")?.Value ?? string.Empty,
            DeviceKind = ReadInputKind(settings?.Attribute("type")?.Value, deviceElement?.Name.LocalName),

            Actions = actions,
        };
    }

    private static InputAction? ReadAction(XElement action)
    {
        var type = action.Attribute("type")?.Value;

        // Empty elements such as <onLeftFast /> mean "nothing bound".
        if (string.IsNullOrEmpty(type)) return null;

        if (type != "XplaneInputAction")
        {
            return new InputAction(InputActionKind.Unsupported, null, null, type);
        }

        var inputType = action.Attribute("inputType")?.Value;

        return new InputAction(
            Kind: inputType == "Command" ? InputActionKind.XplaneCommand : InputActionKind.XplaneDataRef,
            Path: action.Attribute("path")?.Value,
            Expression: action.Attribute("expression")?.Value,
            RawType: type);
    }

    private static OutputDeviceKind ReadDeviceKind(string? displayType) => displayType switch
    {
        "Pin" or "Output" => OutputDeviceKind.Pin,
        "Display Module" or "LedModule" => OutputDeviceKind.DisplayModule,
        "Servo" => OutputDeviceKind.Servo,
        "Stepper" => OutputDeviceKind.Stepper,
        "LcdDisplay" => OutputDeviceKind.LcdDisplay,
        _ => OutputDeviceKind.Unknown,
    };

    private static InputDeviceKind ReadInputKind(string? type, string? elementName)
    {
        if (string.Equals(type, "Button", StringComparison.OrdinalIgnoreCase)) return InputDeviceKind.Button;
        if (string.Equals(type, "Encoder", StringComparison.OrdinalIgnoreCase)) return InputDeviceKind.Encoder;
        if (string.Equals(type, "AnalogInput", StringComparison.OrdinalIgnoreCase)) return InputDeviceKind.AnalogInput;

        return elementName switch
        {
            "button" => InputDeviceKind.Button,
            "encoder" => InputDeviceKind.Encoder,
            "analog" => InputDeviceKind.AnalogInput,
            _ => InputDeviceKind.Unknown,
        };
    }

    private static bool ReadBool(string? value, bool fallback)
    {
        return bool.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static int ReadInt(string? value, int fallback = 0)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        // Some attributes are written in hex.
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
        {
            return hex;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }
}
