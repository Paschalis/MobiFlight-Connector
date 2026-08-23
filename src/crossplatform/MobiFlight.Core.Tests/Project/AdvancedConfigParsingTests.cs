using MobiFlight.Core.Project;

namespace MobiFlight.Core.Tests.Project;

/// <summary>
/// Covers the config features beyond a plain dataref-to-device mapping: preconditions, config
/// references and MobiFlight variables.
/// </summary>
[TestClass]
public sealed class AdvancedConfigParsingTests
{
    private const string Project = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <MobiflightConnector>
          <outputs>
            <config guid="master">
              <active>true</active>
              <description>Battery state</description>
              <settings>
                <source type="XplaneDataRef" path="sim/cockpit/electrical/battery_on" />
                <display type="Pin" serial="Cockpit/ SN-1" pin="2" />
              </settings>
            </config>
            <config guid="dependent">
              <active>true</active>
              <description>Only when battery is on</description>
              <settings>
                <source type="XplaneDataRef" path="sim/cockpit2/annunciators/low_vacuum" />
                <display type="Pin" serial="Cockpit/ SN-1" pin="3" />
                <preconditions>
                  <precondition type="config" active="true" ref="master" operand="=" value="1" logic="and" />
                </preconditions>
              </settings>
            </config>
            <config guid="uses-ref">
              <active>true</active>
              <description>Sum of two configs</description>
              <settings>
                <source type="XplaneDataRef" path="sim/test/value" />
                <display type="Display Module" serial="Cockpit/ SN-1" ledAddress="0" ledConnector="0" ledDigits="0,1" />
                <transformation active="True" expression="$+A" />
                <configrefs>
                  <configref active="True" ref="master" placeholder="A" testvalue="1" />
                </configrefs>
              </settings>
            </config>
            <config guid="from-variable">
              <active>true</active>
              <description>Driven by a MobiFlight variable</description>
              <settings>
                <source type="Variable" varType="number" varName="SelectedPage" varExpression="$" />
                <display type="Pin" serial="Cockpit/ SN-1" pin="4" />
              </settings>
            </config>
            <config guid="needs-arcaze">
              <active>true</active>
              <description>Depends on an Arcaze pin</description>
              <settings>
                <source type="XplaneDataRef" path="sim/test/other" />
                <display type="Pin" serial="Cockpit/ SN-1" pin="5" />
                <preconditions>
                  <precondition type="pin" active="true" serial="Arcaze/ 123" pin="1" operand="=" value="1" logic="and" />
                </preconditions>
              </settings>
            </config>
          </outputs>
          <inputs>
            <config guid="sets-variable">
              <active>true</active>
              <description>Page selector</description>
              <settings serial="Cockpit/ SN-1" name="PageButton" type="Button">
                <button>
                  <onPress type="VariableInputAction" varType="number" varName="SelectedPage" varExpression="$+1" />
                  <onRelease />
                </button>
                <preconditions>
                  <precondition type="variable" active="true" ref="Armed" operand="=" value="1" logic="and" />
                </preconditions>
              </settings>
            </config>
          </inputs>
        </MobiflightConnector>
        """;

    [TestMethod]
    public void ReadsConfigPrecondition()
    {
        var parsed = McConfigReader.ParseXml(Project);
        var dependent = parsed.Outputs.Single(o => o.Guid == "dependent");

        Assert.HasCount(1, dependent.Preconditions);

        var precondition = dependent.Preconditions[0];
        Assert.AreEqual(PreconditionKind.Config, precondition.Kind);
        Assert.IsTrue(precondition.Active);
        Assert.AreEqual("master", precondition.Ref);
        Assert.AreEqual("=", precondition.Operand);
        Assert.AreEqual("1", precondition.Value);
        Assert.AreEqual("and", precondition.Logic);
    }

    [TestMethod]
    public void ReadsConfigReferences()
    {
        var parsed = McConfigReader.ParseXml(Project);
        var usesRef = parsed.Outputs.Single(o => o.Guid == "uses-ref");

        Assert.HasCount(1, usesRef.ConfigReferences);
        Assert.IsTrue(usesRef.ConfigReferences[0].Active);
        Assert.AreEqual("master", usesRef.ConfigReferences[0].Ref);
        Assert.AreEqual("A", usesRef.ConfigReferences[0].Placeholder);
        Assert.AreEqual("$+A", usesRef.Transformation.Expression);
    }

    [TestMethod]
    public void ReadsVariableSource()
    {
        var parsed = McConfigReader.ParseXml(Project);
        var fromVariable = parsed.Outputs.Single(o => o.Guid == "from-variable");

        Assert.AreEqual(ConfigSourceKind.Variable, fromVariable.SourceKind);
        Assert.AreEqual("SelectedPage", fromVariable.VariableName);
        Assert.AreEqual("number", fromVariable.VariableType);
        Assert.IsTrue(fromVariable.IsExecutable, "a variable source is runnable without the sim");
    }

    [TestMethod]
    public void ReadsPinPreconditionAsArcaze()
    {
        var parsed = McConfigReader.ParseXml(Project);
        var arcaze = parsed.Outputs.Single(o => o.Guid == "needs-arcaze");

        Assert.AreEqual(PreconditionKind.Pin, arcaze.Preconditions[0].Kind);
    }

    [TestMethod]
    public void ReadsVariableInputAction()
    {
        var parsed = McConfigReader.ParseXml(Project);
        var input = parsed.Inputs.Single(i => i.Guid == "sets-variable");

        var action = input.Actions["onPress"];
        Assert.AreEqual(InputActionKind.SetVariable, action.Kind);
        Assert.AreEqual("SelectedPage", action.Path);
        Assert.AreEqual("$+1", action.Expression);
        Assert.IsTrue(input.IsExecutable);
    }

    [TestMethod]
    public void ReadsInputPreconditions()
    {
        var parsed = McConfigReader.ParseXml(Project);
        var input = parsed.Inputs.Single(i => i.Guid == "sets-variable");

        Assert.HasCount(1, input.Preconditions);
        Assert.AreEqual(PreconditionKind.Variable, input.Preconditions[0].Kind);
        Assert.AreEqual("Armed", input.Preconditions[0].Ref);
    }

    [TestMethod]
    public void ConfigWithoutPreconditionsGetsEmptyList()
    {
        var parsed = McConfigReader.ParseXml(Project);
        var master = parsed.Outputs.Single(o => o.Guid == "master");

        Assert.IsEmpty(master.Preconditions);
        Assert.IsEmpty(master.ConfigReferences);
    }

    /// <summary>
    /// The end-to-end behaviour these features exist for: a dependent config is held back until the
    /// config it depends on reports the right value.
    /// </summary>
    [TestMethod]
    public void PreconditionGatesDependentConfig()
    {
        var parsed = McConfigReader.ParseXml(Project);
        var dependent = parsed.Outputs.Single(o => o.Guid == "dependent");
        var store = new ConfigValueStore();

        // Before the referenced config has produced anything the Connector leaves the running
        // result untouched, which lets the config through. Replicated for compatibility rather
        // than because it is the more obvious behaviour.
        Assert.IsTrue(store.ArePreconditionsMet(dependent.Preconditions),
            "an unevaluated reference should not block, matching the Connector");

        store.SetConfigValue("master", 0);
        Assert.IsFalse(store.ArePreconditionsMet(dependent.Preconditions), "battery off");

        store.SetConfigValue("master", 1);
        Assert.IsTrue(store.ArePreconditionsMet(dependent.Preconditions), "battery on");
    }

    /// <summary>
    /// A config reference makes another config's value available inside an expression.
    /// </summary>
    [TestMethod]
    public void ConfigReferenceFeedsTransformation()
    {
        var parsed = McConfigReader.ParseXml(Project);
        var usesRef = parsed.Outputs.Single(o => o.Guid == "uses-ref");
        var store = new ConfigValueStore();

        store.SetConfigValue("master", 7);

        var placeholders = store.ResolvePlaceholders(usesRef.ConfigReferences);
        var result = ConfigRunner.Evaluate(usesRef, rawValue: 3, placeholders);

        Assert.AreEqual(10, result, "$ is 3 and A is 7");
    }
}
