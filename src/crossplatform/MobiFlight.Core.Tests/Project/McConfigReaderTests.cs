using MobiFlight.Core.Devices;
using MobiFlight.Core.Project;

namespace MobiFlight.Core.Tests.Project;

[TestClass]
public sealed class McConfigReaderTests
{
    private const string XplaneProject = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <MobiflightConnector>
          <outputs>
            <config guid="out-1">
              <active>true</active>
              <description>Gear Down Light</description>
              <settings>
                <source type="XplaneDataRef" path="sim/cockpit2/annunciators/gear_unsafe" />
                <comparison active="True" value="0" operand="&gt;" ifValue="1" elseValue="0" />
                <display type="Pin" serial="Cockpit/ SN-123-456" pin="13" />
                <transformation active="False" expression="$" />
              </settings>
            </config>
            <config guid="out-2">
              <active>true</active>
              <description>COM1 Frequency</description>
              <settings>
                <source type="XplaneDataRef" path="sim/cockpit/radios/com1_freq_hz" />
                <display type="Display Module" serial="Cockpit/ SN-123-456" ledAddress="0"
                         ledConnector="1" ledDigits="2,3,4,5,6" ledDecimalPoints="4"
                         ledPadding="True" ledPaddingChar="0" />
                <transformation active="True" expression="$/100" />
              </settings>
            </config>
            <config guid="out-3">
              <active>true</active>
              <description>Legacy FSUIPC output</description>
              <settings>
                <source type="FSUIPC" offset="0x034E" />
                <display type="Pin" serial="Cockpit/ SN-123-456" pin="4" />
              </settings>
            </config>
            <config guid="out-4">
              <active>false</active>
              <description>Disabled</description>
              <settings>
                <source type="XplaneDataRef" path="sim/test/disabled" />
                <display type="Pin" serial="Cockpit/ SN-123-456" pin="5" />
              </settings>
            </config>
          </outputs>
          <inputs>
            <config guid="in-1">
              <active>true</active>
              <description>Avionics Switch</description>
              <settings serial="Cockpit/ SN-123-456" name="AvionicsSwitch" type="Button">
                <button>
                  <onPress type="XplaneInputAction" inputType="Command"
                           path="sim/systems/avionics_on" expression="$" />
                  <onRelease type="XplaneInputAction" inputType="DataRef"
                             path="sim/cockpit/electrical/avionics_on" expression="0" />
                </button>
              </settings>
            </config>
            <config guid="in-2">
              <active>true</active>
              <description>COM1 Tuner</description>
              <settings serial="Cockpit/ SN-123-456" name="Com1Encoder" type="Encoder">
                <encoder>
                  <onLeft type="XplaneInputAction" inputType="DataRef"
                          path="sim/cockpit/radios/com1_freq_hz" expression="$-25" />
                  <onLeftFast />
                  <onRight type="XplaneInputAction" inputType="DataRef"
                           path="sim/cockpit/radios/com1_freq_hz" expression="$+25" />
                  <onRightFast />
                </encoder>
              </settings>
            </config>
          </inputs>
        </MobiflightConnector>
        """;

    [TestMethod]
    public void ReadsOutputsAndInputs()
    {
        var project = McConfigReader.ParseXml(XplaneProject);

        Assert.HasCount(4, project.Outputs);
        Assert.HasCount(2, project.Inputs);
    }

    [TestMethod]
    public void ReadsXplaneOutputSource()
    {
        var project = McConfigReader.ParseXml(XplaneProject);
        var gear = project.Outputs.Single(o => o.Guid == "out-1");

        Assert.AreEqual(ConfigSourceKind.XplaneDataRef, gear.SourceKind);
        Assert.AreEqual("sim/cockpit2/annunciators/gear_unsafe", gear.DataRef);
        Assert.AreEqual(OutputDeviceKind.Pin, gear.DeviceKind);
        Assert.AreEqual(13, gear.Pin);
        Assert.AreEqual("Cockpit/ SN-123-456", gear.BoardSerial);
        Assert.IsTrue(gear.Active);
        Assert.IsTrue(gear.IsExecutable);
    }

    [TestMethod]
    public void ReadsComparisonAndTransformation()
    {
        var project = McConfigReader.ParseXml(XplaneProject);

        var gear = project.Outputs.Single(o => o.Guid == "out-1");
        Assert.IsTrue(gear.Comparison.Active);
        Assert.AreEqual(">", gear.Comparison.Operand);
        Assert.AreEqual("0", gear.Comparison.Value);
        Assert.AreEqual("1", gear.Comparison.IfValue);
        Assert.IsFalse(gear.Transformation.Active);

        var com = project.Outputs.Single(o => o.Guid == "out-2");
        Assert.IsTrue(com.Transformation.Active);
        Assert.AreEqual("$/100", com.Transformation.Expression);
    }

    [TestMethod]
    public void ReadsDisplayModuleLayout()
    {
        var project = McConfigReader.ParseXml(XplaneProject);
        var com = project.Outputs.Single(o => o.Guid == "out-2");

        Assert.AreEqual(OutputDeviceKind.DisplayModule, com.DeviceKind);
        Assert.AreEqual(1, com.DisplayConnector);
        Assert.AreEqual("2,3,4,5,6", com.DisplayDigits);
        Assert.AreEqual("4", com.DisplayDecimalPoints);
        Assert.IsTrue(com.DisplayPadding);
        Assert.AreEqual("0", com.DisplayPaddingChar);
    }

    [TestMethod]
    public void MarksNonXplaneSourcesUnsupported()
    {
        var project = McConfigReader.ParseXml(XplaneProject);
        var legacy = project.Outputs.Single(o => o.Guid == "out-3");

        Assert.AreEqual(ConfigSourceKind.Unsupported, legacy.SourceKind);
        Assert.AreEqual("FSUIPC", legacy.RawSourceType);
        Assert.IsFalse(legacy.IsExecutable);

        Assert.ContainsSingle(project.UnsupportedOutputs, "the FSUIPC config should be reported");
    }

    [TestMethod]
    public void InactiveConfigsAreNotExecutable()
    {
        var project = McConfigReader.ParseXml(XplaneProject);

        Assert.IsFalse(project.Outputs.Single(o => o.Guid == "out-4").IsExecutable);
    }

    [TestMethod]
    public void ReadsButtonActions()
    {
        var project = McConfigReader.ParseXml(XplaneProject);
        var button = project.Inputs.Single(i => i.Guid == "in-1");

        Assert.AreEqual(InputDeviceKind.Button, button.DeviceKind);
        Assert.AreEqual("AvionicsSwitch", button.DeviceName);

        Assert.AreEqual(InputActionKind.XplaneCommand, button.Actions["onPress"].Kind);
        Assert.AreEqual("sim/systems/avionics_on", button.Actions["onPress"].Path);

        Assert.AreEqual(InputActionKind.XplaneDataRef, button.Actions["onRelease"].Kind);
        Assert.AreEqual("0", button.Actions["onRelease"].Expression);
    }

    [TestMethod]
    public void SkipsUnboundEncoderEvents()
    {
        var project = McConfigReader.ParseXml(XplaneProject);
        var encoder = project.Inputs.Single(i => i.Guid == "in-2");

        Assert.AreEqual(InputDeviceKind.Encoder, encoder.DeviceKind);
        Assert.IsTrue(encoder.Actions.ContainsKey("onLeft"));
        Assert.IsTrue(encoder.Actions.ContainsKey("onRight"));
        Assert.IsFalse(encoder.Actions.ContainsKey("onLeftFast"), "empty elements mean nothing is bound");
        Assert.AreEqual("$+25", encoder.Actions["onRight"].Expression);
    }

    [TestMethod]
    public void HandlesProjectWithoutSections()
    {
        var project = McConfigReader.ParseXml("<MobiflightConnector></MobiflightConnector>");

        Assert.IsEmpty(project.Outputs);
        Assert.IsEmpty(project.Inputs);
    }

    /// <summary>
    /// The real example files shipped with the Connector must at least parse without throwing.
    /// </summary>
    [TestMethod]
    public void ParsesBundledExampleProjects()
    {
        var examples = FindExamplesDirectory();

        if (examples is null)
        {
            Assert.Inconclusive("Example projects not found relative to the test output directory.");
            return;
        }

        var files = Directory.GetFiles(examples, "*.mcc");
        Assert.IsNotEmpty(files, "expected at least one bundled example");

        foreach (var file in files)
        {
            var project = McConfigReader.Load(file);

            Assert.IsNotNull(project, $"{Path.GetFileName(file)} failed to parse");

            // Every output must land in a known state rather than throwing or silently vanishing.
            foreach (var output in project.Outputs)
            {
                Assert.IsNotNull(output.Description, $"{Path.GetFileName(file)} produced a null description");
            }
        }
    }

    private static string? FindExamplesDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "MobiFlightConnector", "examples");
            if (Directory.Exists(candidate)) return candidate;

            directory = directory.Parent;
        }

        return null;
    }
}

[TestClass]
public sealed class ConfigRunnerLogicTests
{
    [TestMethod]
    public void ExtractsSerialFromConfigFormat()
    {
        Assert.AreEqual("SN-849-180", ConfigRunner.ExtractSerial("TestBoard/ SN-849-180"));
        Assert.AreEqual("SN-1", ConfigRunner.ExtractSerial("SN-1"));
        Assert.AreEqual("SN-2", ConfigRunner.ExtractSerial("A/B/ SN-2"));
    }

    [TestMethod]
    public void BuildsDigitMaskFromDigitList()
    {
        // Digits 2,3,4 -> bits 2,3,4 set.
        Assert.AreEqual(0b0001_1100, ConfigRunner.BuildDigitMask("2,3,4"));
        Assert.AreEqual(0b0000_0001, ConfigRunner.BuildDigitMask("0"));
        Assert.AreEqual(0xFF, ConfigRunner.BuildDigitMask(null, 0xFF), "falls back when unset");
        Assert.AreEqual(0, ConfigRunner.BuildDigitMask("99"), "out of range digits are ignored");
    }

    [TestMethod]
    public void AppliesTransformationThenComparison()
    {
        var config = new OutputConfig
        {
            Guid = "g",
            Description = "d",
            Active = true,
            Transformation = new TransformationRule(true, "$/100"),
            Comparison = new ComparisonRule(true, ">", "100", "1", "0"),
        };

        // 12180 / 100 = 121.8, which is > 100, so the if-branch wins.
        Assert.AreEqual(1, ConfigRunner.Evaluate(config, 12180));

        // 5000 / 100 = 50, which is not > 100.
        Assert.AreEqual(0, ConfigRunner.Evaluate(config, 5000));
    }

    [TestMethod]
    public void ComparisonBranchCanReferenceValue()
    {
        var config = new OutputConfig
        {
            Guid = "g",
            Description = "d",
            Active = true,
            Comparison = new ComparisonRule(true, "=", "0", "$+10000", "$"),
        };

        Assert.AreEqual(10000, ConfigRunner.Evaluate(config, 0));
        Assert.AreEqual(50, ConfigRunner.Evaluate(config, 50));
    }

    [TestMethod]
    public void EmptyBranchLeavesValueUnchanged()
    {
        var config = new OutputConfig
        {
            Guid = "g",
            Description = "d",
            Active = true,
            Comparison = new ComparisonRule(true, ">", "10", "", ""),
        };

        Assert.AreEqual(42, ConfigRunner.Evaluate(config, 42));
    }

    [TestMethod]
    public void MapsButtonEventsToActionNames()
    {
        var config = ButtonConfig("onPress", "onRelease");

        Assert.AreEqual("onPress", ConfigRunner.ResolveEventName(config, Input(BoardInputType.Button, 0)));
        Assert.AreEqual("onRelease", ConfigRunner.ResolveEventName(config, Input(BoardInputType.Button, 1)));

        // Long release falls back to onRelease when not separately bound.
        Assert.AreEqual("onRelease", ConfigRunner.ResolveEventName(config, Input(BoardInputType.Button, 2)));
    }

    [TestMethod]
    public void MapsEncoderEventsWithFastFallback()
    {
        var slowOnly = ButtonConfig("onLeft", "onRight");

        Assert.AreEqual("onLeft", ConfigRunner.ResolveEventName(slowOnly, Input(BoardInputType.Encoder, 0)));
        Assert.AreEqual("onLeft", ConfigRunner.ResolveEventName(slowOnly, Input(BoardInputType.Encoder, 1)));
        Assert.AreEqual("onRight", ConfigRunner.ResolveEventName(slowOnly, Input(BoardInputType.Encoder, 2)));
        Assert.AreEqual("onRight", ConfigRunner.ResolveEventName(slowOnly, Input(BoardInputType.Encoder, 3)));

        var withFast = ButtonConfig("onLeft", "onRight", "onLeftFast", "onRightFast");

        Assert.AreEqual("onLeftFast", ConfigRunner.ResolveEventName(withFast, Input(BoardInputType.Encoder, 1)));
        Assert.AreEqual("onRightFast", ConfigRunner.ResolveEventName(withFast, Input(BoardInputType.Encoder, 3)));
    }

    private static InputConfig ButtonConfig(params string[] events)
    {
        return new InputConfig
        {
            Guid = "g",
            Description = "d",
            Active = true,
            DeviceName = "Device",
            Actions = events.ToDictionary(
                e => e,
                _ => new InputAction(InputActionKind.XplaneCommand, "sim/test", "$", "XplaneInputAction"),
                StringComparer.OrdinalIgnoreCase),
        };
    }

    private static BoardInputEventArgs Input(BoardInputType type, int value)
    {
        return new BoardInputEventArgs("Device", type, value);
    }
}
